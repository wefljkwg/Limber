// Limber relay - runs on YOUR OWN free Cloudflare account, deployed by the Limber plugin.
//
// What it does: passes already-encrypted bytes between your game and your sync partner's relay.
// What it never does: read your data (it can't - it's encrypted end-to-end), store anything except
// the current invite room, log anything, or pass IP addresses on. Your partner's relay only ever talks
// to this relay, so it only ever sees Cloudflare's addresses - never your home IP, and you never see theirs.
//
//   your game  <->  /owner  [this relay]  /peer or outbound  <->  partner's relay  <->  partner's game

const VERSION = 1;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return new Response("limber-relay " + VERSION, { headers: { "cache-control": "no-store" } });
    }

    if (url.pathname === "/owner" || url.pathname === "/peer") {
      if ((request.headers.get("Upgrade") || "").toLowerCase() !== "websocket") {
        return new Response("expected websocket", { status: 426 });
      }
      // Hand the connection to the relay object with ONLY the headers it needs. The caller's IP and
      // every other header are dropped here, so the relay logic never even receives them.
      const headers = new Headers({ Upgrade: "websocket" });
      for (const name of ["x-lmb-token", "x-lmb-room", "x-lmb-auth"]) {
        const value = request.headers.get(name);
        if (value) headers.set(name, value);
      }
      const stub = env.RELAY.get(env.RELAY.idFromName("home"));
      return stub.fetch(new Request("https://relay.internal" + url.pathname, { headers }));
    }

    return new Response("not found", { status: 404 });
  },
};

const enc = new TextEncoder();

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", enc.encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function sameSecret(a, b) {
  const x = enc.encode(a || "");
  const y = enc.encode(b || "");
  if (x.length !== y.length || x.length === 0) return false;
  return crypto.subtle.timingSafeEqual(x, y);
}

export class Relay {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.out = null;   // outbound connection to a partner's relay (when we joined their invite)
    this.room = null;  // { id, authHash } for our open invite
    ctx.blockConcurrencyWhile(async () => {
      this.room = (await ctx.storage.get("room")) || null;
    });
  }

  async fetch(request) {
    const path = new URL(request.url).pathname;

    if (path === "/owner") {
      if (!sameSecret(request.headers.get("x-lmb-token"), this.env.OWNER_TOKEN)) {
        return new Response("forbidden", { status: 403 });
      }
      for (const ws of this.ctx.getWebSockets("owner")) {
        try { ws.close(4000, "replaced by a new connection"); } catch {}
      }
      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, ["owner"]);
      server.send(JSON.stringify({ t: "hello", version: VERSION, peer: this.peerConnected() }));
      return new Response(null, { status: 101, webSocket: client });
    }

    if (path === "/peer") {
      const roomId = request.headers.get("x-lmb-room") || "";
      const auth = request.headers.get("x-lmb-auth") || "";
      if (!this.room || !sameSecret(roomId, this.room.id) || !sameSecret(await sha256Hex(auth), this.room.authHash)) {
        return new Response("invite not found", { status: 403 });
      }
      if (this.ctx.getWebSockets("owner").length === 0) return new Response("host offline", { status: 503 });
      if (this.peerConnected()) return new Response("busy", { status: 409 });

      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, ["peer"]);
      this.sendOwner({ t: "peer_connected" });
      return new Response(null, { status: 101, webSocket: client });
    }

    return new Response("not found", { status: 404 });
  }

  peerConnected() {
    return this.out !== null || this.ctx.getWebSockets("peer").length > 0;
  }

  sendOwner(obj) {
    const text = JSON.stringify(obj);
    for (const ws of this.ctx.getWebSockets("owner")) {
      try { ws.send(text); } catch {}
    }
  }

  toOwner(bytes) {
    for (const ws of this.ctx.getWebSockets("owner")) {
      try { ws.send(bytes); } catch {}
    }
  }

  toPeer(bytes) {
    if (this.out) {
      try { this.out.send(bytes); } catch {}
      return;
    }
    for (const ws of this.ctx.getWebSockets("peer")) {
      try { ws.send(bytes); } catch {}
    }
  }

  dropPeer(reason) {
    if (this.out) {
      try { this.out.close(1000, reason); } catch {}
      this.out = null;
    }
    for (const ws of this.ctx.getWebSockets("peer")) {
      try { ws.close(1000, reason); } catch {}
    }
  }

  async webSocketMessage(ws, message) {
    const tags = this.ctx.getTags(ws);
    if (tags.includes("owner")) {
      if (typeof message === "string") {
        let cmd;
        try { cmd = JSON.parse(message); } catch { return; }
        await this.command(cmd);
      } else {
        this.toPeer(message); // encrypted bytes from your game -> partner
      }
    } else if (tags.includes("peer")) {
      if (typeof message !== "string") this.toOwner(message); // partner -> your game
    }
  }

  async command(cmd) {
    switch (cmd && cmd.t) {
      case "open_room":
        if (typeof cmd.room !== "string" || typeof cmd.authHash !== "string") return;
        this.dropPeer("new invite");
        this.room = { id: cmd.room, authHash: cmd.authHash };
        await this.ctx.storage.put("room", this.room);
        this.sendOwner({ t: "room_open" });
        break;
      case "close_room":
        this.room = null;
        await this.ctx.storage.delete("room");
        this.dropPeer("invite closed");
        this.sendOwner({ t: "room_closed" });
        break;
      case "join":
        await this.join(cmd.url, cmd.room, cmd.auth);
        break;
      case "leave":
        this.dropPeer("left");
        this.sendOwner({ t: "peer_closed" });
        break;
      case "ping":
        this.sendOwner({ t: "pong" });
        break;
    }
  }

  async join(address, roomId, auth) {
    this.dropPeer("joining another relay");
    try {
      const url = new URL(address);
      // Only ever connect to another Limber relay on workers.dev - never an arbitrary server.
      if (url.protocol !== "https:" || !url.hostname.endsWith(".workers.dev")) throw new Error("not a relay address");
      url.pathname = "/peer";
      url.search = "";
      const response = await fetch(url.toString(), {
        headers: { Upgrade: "websocket", "x-lmb-room": String(roomId || ""), "x-lmb-auth": String(auth || "") },
      });
      const ws = response.webSocket;
      if (!ws) {
        const reason = response.status === 403 ? "invite not found or expired"
          : response.status === 409 ? "they are already synced with someone"
          : response.status === 503 ? "their game isn't connected to their relay"
          : "their relay refused (" + response.status + ")";
        throw new Error(reason);
      }
      ws.accept();
      this.out = ws;
      ws.addEventListener("message", (event) => {
        if (typeof event.data !== "string") this.toOwner(event.data);
      });
      const closed = () => {
        if (this.out === ws) {
          this.out = null;
          this.sendOwner({ t: "peer_closed" });
        }
      };
      ws.addEventListener("close", () => { try { ws.close(); } catch {} closed(); });
      ws.addEventListener("error", closed);
      this.sendOwner({ t: "joined" });
    } catch (e) {
      this.sendOwner({ t: "join_failed", reason: String((e && e.message) || e) });
    }
  }

  async webSocketClose(ws, code, reason) {
    try { ws.close(code, reason); } catch {}
    const tags = this.ctx.getTags(ws);
    if (tags.includes("peer")) this.sendOwner({ t: "peer_closed" });
    if (tags.includes("owner") && this.ctx.getWebSockets("owner").length <= 1) this.dropPeer("owner left");
  }

  async webSocketError(ws) {
    const tags = this.ctx.getTags(ws);
    if (tags.includes("peer")) this.sendOwner({ t: "peer_closed" });
  }
}
