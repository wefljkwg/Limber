namespace Limber.UI;

/// <summary>Plain-language help shown by the Sync tab's "Instructions" button.</summary>
public static class SyncHelpText
{
    public static readonly (string Title, string[] Lines)[] Sections =
    [
        ("Setup (once, about 3 minutes)",
        [
            "Both players do this once on their own PC.",
            "1. Make a free Cloudflare account at dash.cloudflare.com (just an email address, no credit card).",
            "2. In Cloudflare, click your profile icon (top right) > My Profile > API Tokens > Create Token.",
            "3. Next to \"Edit Cloudflare Workers\", click Use template, then Continue to summary, then Create Token.",
            "4. Copy the token Cloudflare shows you.",
            "5. In Limber's Koikatsu link page (Sync), paste it into \"Cloudflare API token\" and click Deploy my relay.",
            "6. Wait for \"Relay ready\". Done. The token isn't saved - you can delete it in Cloudflare afterwards.",
            "If Limber asks for an Account ID: it's on the right side of Cloudflare's \"Workers & Pages\" page.",
            "If it asks you to finish Workers setup: open the link it shows, click through, and press Deploy again.",
        ]),
        ("Syncing with someone",
        [
            "1. Both players tick \"Connect to my relay\". The player sending the invite also ticks \"Accept sync requests\".",
            "2. Player A clicks Create invite code and sends the code to player B any way they like.",
            "3. Player B pastes the code, picks \"I'll host\" or \"Follow them\", and clicks Send request.",
            "4. Player A sees the request and clicks Accept (or Decline).",
            "5. That's it. The host's Koikatsu now moves both characters - on both screens.",
            "Either player can press Swap host; the other player is asked first. End sync stops it for both.",
            "The host chooses which Koikatsu channel moves which character in the Sync section.",
            "Syncing works in GPose. To also sync in the open world, both players tick \"Sync outside GPose too\".",
        ]),
        ("How it works",
        [
            "The host runs Koikatsu. Limber reads the host's Koikatsu poses, face and animation, like it always does.",
            "It also sends those poses to the other player. The other player's Limber applies them to both characters on their screen, so both of you see the same thing. The follower doesn't need Koikatsu at all.",
            "The poses travel through two small relays: yours and your partner's. Each relay is a tiny program running on that player's own free Cloudflare account. Your game only ever talks to your own relay, and the two relays talk to each other.",
            "Nothing goes through Square Enix's servers or game chat. Other players never see any of this - only the two of you, on your own screens.",
            "It's free: each relay runs within Cloudflare's free allowance. If a relay ever hits that day's limit, sync simply pauses until the next day. Nobody is charged.",
        ]),
        ("How your privacy is protected",
        [
            "Your partner never sees your IP address, and you never see theirs. Your game connects only to your own relay. When the two relays connect to each other, each one only sees a Cloudflare address - never the player behind it. Your relay drops the connecting IP address straight away and never stores it or passes it on.",
            "Everything is encrypted end to end (AES-256-GCM) with a key that only exists in the invite code. The relays only pass along scrambled bytes they can't read - not even your own relay can see your poses.",
            "Nothing is stored or logged. The relay only remembers your current invite code, so it can check it. When you end the sync, that's forgotten.",
            "Only people you choose can connect. Without the full invite code nobody can reach you; you must tick \"Accept sync requests\" and click Accept for each request; and a relay will only connect to other relay addresses (workers.dev), never to anything else.",
            "What your partner does learn: your character name and home world (they're with you in game anyway), your poses while synced, and your relay's web address (it's in the invite code). New relays get a random address like lmb-1a2b3c; if your Cloudflare account already had a workers.dev name, Limber tells you so you can rename it if it's personal.",
            "What Cloudflare can see: like any website or app (Discord, browsers...), Cloudflare sees the IP addresses connecting to its servers. It cannot read your encrypted poses.",
            "Your Cloudflare API token is only used once to set up your relay and is never saved by Limber.",
        ]),
    ];
}
