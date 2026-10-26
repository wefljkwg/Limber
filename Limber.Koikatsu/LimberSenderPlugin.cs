using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKAPI;
using KKAPI.Studio;
using Limber.Protocol;
using UnityEngine;
using Joint = Limber.Protocol.Joint;

namespace Limber.Koikatsu
{
    /// <summary>
    /// Streams the pose, facial expression and gaze of up to 8 Koikatsu characters to the Limber
    /// Dalamud plugin over localhost UDP. Each character goes out on its own channel (1-8); in FFXIV
    /// each channel can drive a different GPose actor. Sampling happens at the end of the frame so
    /// FK/IK, Studio posing and animation have all been applied.
    ///
    /// Channel assignment:
    ///  * Nothing linked: the selected Studio character (or the first character in the main game)
    ///    streams on channel 1, so the single-character workflow needs no setup.
    ///  * Ctrl+F10: link / unlink the selected character on the next free channel.
    ///  * Ctrl+F11: link every character in the scene, in scene order, to channels 1..8.
    ///  * Ctrl+Shift+F10: clear all links.
    ///  * Ctrl+F8: pause / resume every character's animation (FFXIV's Limber can do the same).
    ///  * Ctrl+F12: the channel window (which FFXIV actor each channel drives; assign characters to channels).
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency(KoikatuAPI.GUID, "1.20")]
    // Studio is the main target; the main game / maker are supported too.
    [BepInProcess("CharaStudio")]
    [BepInProcess("Koikatu")]
    [BepInProcess("Koikatsu Party")]
    [BepInProcess("KoikatsuSunshine")]
    public class LimberSenderPlugin : BaseUnityPlugin
    {
        public const string GUID = "limber.koikatsu.sender";
        public const string PluginName = "Limber Koikatsu Sender";
        public const string Version = "0.11.0";

        internal static new ManualLogSource Logger;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<int> _port;
        private ConfigEntry<int> _sendRate;
        private ConfigEntry<bool> _showStatus;
        private ConfigEntry<bool> _sendFace;
        private GUIStyle _overlayStyle;
        private readonly StringBuilder _overlay = new StringBuilder();
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<KeyboardShortcut> _linkKey;
        private ConfigEntry<KeyboardShortcut> _linkAllKey;
        private ConfigEntry<KeyboardShortcut> _clearKey;
        private ConfigEntry<KeyboardShortcut> _pauseKey;
        private ConfigEntry<KeyboardShortcut> _windowKey;

        // What FFXIV's Limber says each channel drives (control packets, loopback only), for the channel window.
        private sealed class FfxivLink
        {
            public int Channel;
            public bool Active;
            public string Name;
        }
        private volatile FfxivLink[] _ffxivLinks = new FfxivLink[0];
        private long _ffxivLinksAt; // Environment.TickCount when last heard (ms)
        private bool _showWindow;
        private Rect _windowRect = new Rect(40, 120, 460, 420);
        private Vector2 _windowScroll;
        private int _pickingFor = -1; // channel whose character list is open

        // Pause / resume, from the hotkey or from FFXIV (control packets on loopback only).
        private readonly AnimationPauser _pauser = new AnimationPauser();
        private volatile UdpClient _control;
        private int _controlPort;
        private int _pendingCommand;

        private UdpClient _udp;
        private IPEndPoint _endPoint;
        private int _boundPort;

        private readonly MemoryStream _buffer = new MemoryStream(Wire.MaxPacketSize);
        private BinaryWriter _writer;
        private uint _frame;
        private float _nextSendTime;

        /// <summary>Manually linked characters; index = channel. All null = follow selection on channel 1.</summary>
        private readonly ChaControl[] _linked = new ChaControl[Wire.MaxChannels];
        private readonly ChannelState[] _channels = new ChannelState[Wire.MaxChannels];
        private readonly FaceReader _face = new FaceReader();

        private ChaControl _lastStudioSelection;
        private ChaControl _autoCharacter;
        private float _nextSceneScan;
        private string _status = "idle";

        private sealed class ChannelState
        {
            public ChaControl Character;
            public readonly BoneCache Bones = new BoneCache();
            public readonly FaceRegionSampler Face = new FaceRegionSampler();
            public string Info = "";

            // Breast rest direction per side, in the chest bone's local frame.
            public readonly Vector3[] BustRest = new Vector3[2];
            public readonly bool[] HasBustRest = new bool[2];
            public float LastSampleTime;
        }

        private void Awake()
        {
            Logger = base.Logger;

            _enabled = Config.Bind("General", "Enabled", true, "Stream linked characters to FFXIV.");
            _port = Config.Bind("General", "Port", Wire.DefaultPort,
                new ConfigDescription("UDP port on 127.0.0.1. Must match the port set in the Dalamud plugin.",
                    new AcceptableValueRange<int>(1024, 65535)));
            _sendRate = Config.Bind("General", "Max send rate (fps)", 120,
                new ConfigDescription("Maximum frames per second (each frame sends one packet per linked character).",
                    new AcceptableValueRange<int>(10, 240)));
            _showStatus = Config.Bind("General", "Show status overlay", true, "Status lines in the bottom-right corner.");
            _sendFace = Config.Bind("General", "Send facial expressions", true,
                "Measure the face mesh so FFXIV can mirror expressions (eyelids, brows, mouth, cheeks).");
            _toggleKey = Config.Bind("Hotkeys", "Toggle streaming", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl));
            _linkKey = Config.Bind("Hotkeys", "Link / unlink selected character", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Assigns the selected character to the next free channel, or removes it if already linked.");
            _linkAllKey = Config.Bind("Hotkeys", "Link all characters in scene", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl),
                "Assigns every character in the scene to channels 1-8 in scene order.");
            _clearKey = Config.Bind("Hotkeys", "Clear all links", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl, KeyCode.LeftShift));
            _pauseKey = Config.Bind("Hotkeys", "Pause / resume animation", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Pauses or resumes the animation of every character in the scene. Limber in FFXIV has the same button.");
            _windowKey = Config.Bind("Hotkeys", "Show channel window", new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl),
                "Shows the FFXIV channels (and which FFXIV actor each drives), to assign Koikatsu characters to them.");

            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new ChannelState();

            _writer = new BinaryWriter(_buffer);
            OpenSocket();
        }

        private void OpenSocket()
        {
            CloseSocket();
            _boundPort = _port.Value;
            OpenControl();
            try
            {
                // Bound to loopback and sending only to loopback: packets can never leave this PC.
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _endPoint = new IPEndPoint(IPAddress.Loopback, _boundPort);
            }
            catch (Exception e)
            {
                Logger.LogError("Could not open UDP socket: " + e);
                _udp = null;
            }
        }

        /// <summary>Listen on 127.0.0.1 only, for pause / resume requests from Limber in FFXIV.</summary>
        private void OpenControl()
        {
            CloseControl();
            _controlPort = Wire.ControlPort(_boundPort);
            try
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _controlPort));
                client.Client.ReceiveTimeout = 500;
                _control = client;
                var thread = new System.Threading.Thread(() => ControlLoop(client)) { IsBackground = true, Name = "Limber control" };
                thread.Start();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Limber: can't listen for pause / resume from FFXIV on port " + _controlPort + " (" + e.Message + "). Ctrl+F8 still works.");
                _control = null;
            }
        }

        private void ControlLoop(UdpClient client)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_control == client)
            {
                try
                {
                    var data = client.Receive(ref from);
                    if (data != null && data.Length >= 5 && BitConverter.ToUInt32(data, 0) == Wire.ControlMagic &&
                        IPAddress.IsLoopback(from.Address))
                    {
                        if (data[4] == Wire.CmdLinks)
                            ReadLinks(data);
                        else
                            System.Threading.Interlocked.Exchange(ref _pendingCommand, data[4]);
                    }
                }
                catch (SocketException)
                {
                    // Receive timeout (or the socket closing): check whether to keep going.
                }
                catch (Exception)
                {
                    return; // socket closed
                }
            }
        }

        // Which FFXIV actor each channel drives (see Protocol: CmdLinks). Control thread.
        private void ReadLinks(byte[] data)
        {
            try
            {
                int at = 5;
                int count = data[at++];
                var list = new List<FfxivLink>();
                for (int i = 0; i < count && at + 4 <= data.Length; i++)
                {
                    int channel = data[at++];
                    bool active = data[at++] != 0;
                    int length = BitConverter.ToUInt16(data, at);
                    at += 2;
                    if (at + length > data.Length || length > 256)
                        break;
                    string name = Encoding.UTF8.GetString(data, at, length);
                    at += length;
                    if (channel < Wire.MaxChannels)
                        list.Add(new FfxivLink { Channel = channel, Active = active, Name = name });
                }
                _ffxivLinks = list.ToArray();
                System.Threading.Interlocked.Exchange(ref _ffxivLinksAt, Environment.TickCount);
            }
            catch
            {
                // malformed: ignore
            }
        }

        private void CloseControl()
        {
            var c = _control;
            _control = null;
            if (c != null)
            {
                try { c.Close(); } catch { }
            }
        }

        private void SetAnimationPaused(bool paused)
        {
            if (paused == _pauser.Paused)
                return;
            _pauser.SetPaused(paused);
            Logger.LogMessage(paused ? "Limber: animation paused (Ctrl+F8 to resume)" : "Limber: animation resumed");
        }

        private void CloseSocket()
        {
            if (_udp != null)
            {
                try { _udp.Close(); } catch { }
                _udp = null;
            }
        }

        private void Start()
        {
            StartCoroutine(SendLoop());
            StartCoroutine(PauseLoop());
        }

        private void OnDestroy()
        {
            _pauser.SetPaused(false);
            CloseControl();
            CloseSocket();
        }

        /// <summary>Runs after every script's Update and before animation, so the pause wins each frame.</summary>
        private IEnumerator PauseLoop()
        {
            while (true)
            {
                yield return null;
                _pauser.Enforce();
            }
        }

        // ------------------------------------------------------------------------------------
        // Hotkeys / linking

        private void Update()
        {
            // Check the more specific shortcut first so Ctrl+Shift+F10 doesn't also trigger Ctrl+F10.
            if (_clearKey.Value.IsDown())
            {
                ClearLinks();
            }
            else if (_linkKey.Value.IsDown())
            {
                ToggleLinkSelected();
            }

            if (_linkAllKey.Value.IsDown())
                LinkAll();

            if (_pauseKey.Value.IsDown())
                SetAnimationPaused(!_pauser.Paused);

            if (_windowKey.Value.IsDown())
                _showWindow = !_showWindow;

            switch (System.Threading.Interlocked.Exchange(ref _pendingCommand, 0))
            {
                case Wire.CmdPause: SetAnimationPaused(true); break;
                case Wire.CmdResume: SetAnimationPaused(false); break;
                case Wire.CmdToggle: SetAnimationPaused(!_pauser.Paused); break;
            }

            if (_toggleKey.Value.IsDown())
            {
                _enabled.Value = !_enabled.Value;
                Logger.LogMessage("Limber streaming " + (_enabled.Value ? "ON" : "OFF"));
            }

            if (_boundPort != _port.Value)
                OpenSocket();
        }

        private bool AnyLinked()
        {
            for (int i = 0; i < _linked.Length; i++)
                if (_linked[i] != null) // Unity null: destroyed characters drop out automatically
                    return true;
            return false;
        }

        private void ToggleLinkSelected()
        {
            var selected = SelectedCharacter();
            if (selected == null)
            {
                Logger.LogMessage("Limber: select a character first");
                return;
            }

            for (int i = 0; i < _linked.Length; i++)
            {
                if (_linked[i] == selected)
                {
                    _linked[i] = null;
                    Logger.LogMessage("Limber: unlinked " + SafeName(selected) + " from channel " + (i + 1));
                    return;
                }
            }

            for (int i = 0; i < _linked.Length; i++)
            {
                if (_linked[i] == null)
                {
                    _linked[i] = selected;
                    Logger.LogMessage("Limber: " + SafeName(selected) + " -> channel " + (i + 1));
                    return;
                }
            }
            Logger.LogMessage("Limber: all " + Wire.MaxChannels + " channels are in use");
        }

        private void LinkAll()
        {
            ClearLinks();
            var all = AllCharacters();
            for (int i = 0; i < all.Count && i < _linked.Length; i++)
                _linked[i] = all[i];
            Logger.LogMessage("Limber: linked " + Math.Min(all.Count, _linked.Length) + " character(s)" +
                              (all.Count > _linked.Length ? " (only the first " + _linked.Length + " fit)" : ""));
        }

        private void ClearLinks()
        {
            for (int i = 0; i < _linked.Length; i++)
                _linked[i] = null;
            Logger.LogMessage("Limber: links cleared, following selection on channel 1");
        }

        private ChaControl SelectedCharacter()
        {
            if (StudioAPI.InsideStudio)
            {
                foreach (var oci in StudioAPI.GetSelectedCharacters())
                {
                    var c = oci.GetChaControl();
                    if (c != null)
                        return c;
                }
                return null;
            }
            return FirstSceneCharacter();
        }

        private static List<ChaControl> AllCharacters()
        {
            var result = new List<ChaControl>();
            if (StudioAPI.InsideStudio)
            {
                // Scene order (object keys increase as characters are added).
                foreach (var kv in global::Studio.Studio.Instance.dicObjectCtrl.OrderBy(x => x.Key))
                {
                    var oci = kv.Value as global::Studio.OCIChar;
                    var c = oci != null ? oci.GetChaControl() : null;
                    if (c != null && !result.Contains(c))
                        result.Add(c);
                }
                return result;
            }

            foreach (var c in FindObjectsOfType<ChaControl>())
            {
                if (c != null && c.gameObject.activeInHierarchy && c.objBodyBone != null)
                    result.Add(c);
            }
            return result;
        }

        private static ChaControl FirstSceneCharacter()
        {
            foreach (var c in FindObjectsOfType<ChaControl>())
            {
                if (c != null && c.gameObject.activeInHierarchy && c.objBodyBone != null)
                    return c;
            }
            return null;
        }

        /// <summary>Character for channel 1 when nothing is linked: follows the Studio selection.</summary>
        private ChaControl AutoCharacter()
        {
            if (StudioAPI.InsideStudio)
            {
                var sel = SelectedCharacter();
                if (sel != null)
                    _lastStudioSelection = sel;
                // Keep streaming the last selected character while a light, item or camera is selected.
                return _lastStudioSelection;
            }

            if (_autoCharacter != null && _autoCharacter.gameObject.activeInHierarchy)
                return _autoCharacter;
            if (Time.unscaledTime < _nextSceneScan)
                return null;
            _nextSceneScan = Time.unscaledTime + 1f;
            _autoCharacter = FirstSceneCharacter();
            return _autoCharacter;
        }

        // ------------------------------------------------------------------------------------
        // Sending

        private IEnumerator SendLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;

                if (!_enabled.Value || _udp == null)
                {
                    _status = _enabled.Value ? "socket error" : "paused (Ctrl+F9)";
                    continue;
                }

                // Rate cap with a little tolerance, so frame-time jitter near the cap never drops frames.
                float interval = 1f / Mathf.Max(1, _sendRate.Value);
                float now = Time.unscaledTime;
                if (now + interval * 0.25f < _nextSendTime)
                    continue;
                _nextSendTime = Mathf.Max(_nextSendTime + interval, now);

                var manual = AnyLinked();
                int sent = 0;
                _frame++;

                for (int ch = 0; ch < _channels.Length; ch++)
                {
                    var state = _channels[ch];
                    var chara = manual ? _linked[ch] : (ch == 0 ? AutoCharacter() : null);
                    if (chara == null) // also catches characters deleted from the scene
                    {
                        if (manual) _linked[ch] = null;
                        state.Character = null;
                        state.Bones.Clear();
                        state.Face.Clear();
                        state.HasBustRest[0] = state.HasBustRest[1] = false;
                        state.LastSampleTime = 0f;
                        state.Info = "";
                        continue;
                    }

                    try
                    {
                        if (chara != state.Character)
                        {
                            state.Character = chara;
                            state.Bones.Clear();
                            state.Face.Clear();
                            state.HasBustRest[0] = state.HasBustRest[1] = false;
                            state.LastSampleTime = 0f;
                        }
                        if (!state.Bones.IsValidFor(chara))
                            state.Bones.Build(chara);
                        if (_sendFace.Value)
                            state.Face.EnsureFor(chara, state.Bones);

                        var size = WritePacket(chara, state, ch);
                        _udp.Send(_buffer.GetBuffer(), size, _endPoint);
                        sent++;
                        state.Info = SafeName(chara) + (state.Face.IsReady ? "  face" : "") + "  " + state.Bones.FoundCount + "/" + (int)Joint.DanP0 + " joints" +
                                     (state.Bones.DanChain.Count >= 2 ? " + genitals" : "");
                    }
                    catch (Exception e)
                    {
                        state.Info = "error (see log)";
                        state.Bones.Clear();
                        Logger.LogError(e);
                    }
                }

                _status = sent == 0
                    ? (manual ? "linked characters missing" : "no character selected")
                    : "sending " + sent + " character(s)" + (manual ? "" : " (following selection; Ctrl+F10 to link)");
            }
        }

        private int WritePacket(ChaControl chara, ChannelState state, int channel)
        {
            var bones = state.Bones;
            UpdateBustRest(state);
            _buffer.Position = 0;
            var w = _writer;
            var root = chara.transform;

            w.Write(Wire.Magic);
            w.Write(Wire.Version);
            w.Write((ushort)Joint.Count);
            w.Write(_frame);
            w.Write((byte)channel);

            var hasDan = ResampleChain(bones.DanChain, _danPoints);
            var spine = bones.Get(Joint.Spine3);

            for (int i = 0; i < (int)Joint.Count; i++)
            {
                if (i >= (int)Joint.DanP0 && i <= (int)Joint.DanP6)
                {
                    if (!hasDan)
                    {
                        w.Write((byte)0);
                        w.Write(0f); w.Write(0f); w.Write(0f);
                        continue;
                    }
                    var dp = root.InverseTransformPoint(_danPoints[i - (int)Joint.DanP0]);
                    w.Write((byte)1);
                    w.Write(dp.x); w.Write(dp.y); w.Write(dp.z);
                    continue;
                }

                if (i == (int)Joint.BustRestL || i == (int)Joint.BustRestR)
                {
                    int side = i == (int)Joint.BustRestL ? 0 : 1;
                    var bRoot = bones.Get(side == 0 ? Joint.BustRootL : Joint.BustRootR);
                    var bTip = bones.Get(side == 0 ? Joint.BustTipL : Joint.BustTipR);
                    if (spine == null || bRoot == null || bTip == null || !state.HasBustRest[side])
                    {
                        w.Write((byte)0);
                        w.Write(0f); w.Write(0f); w.Write(0f);
                        continue;
                    }
                    var len = Vector3.Distance(bRoot.position, bTip.position);
                    var restWorld = bRoot.position + spine.TransformDirection(state.BustRest[side]) * len;
                    var rp = root.InverseTransformPoint(restWorld);
                    w.Write((byte)1);
                    w.Write(rp.x); w.Write(rp.y); w.Write(rp.z);
                    continue;
                }

                if ((i == (int)Joint.EyeL || i == (int)Joint.EyeR) && bones.HasEyeLocal && bones.Get(Joint.Head) != null)
                {
                    // Eye points fixed to the head (see BoneCache.EyeLocalL), so only head motion moves them.
                    var headT = bones.Get(Joint.Head);
                    var ep = root.InverseTransformPoint(headT.TransformPoint(i == (int)Joint.EyeL ? bones.EyeLocalL : bones.EyeLocalR));
                    w.Write((byte)1);
                    w.Write(ep.x); w.Write(ep.y); w.Write(ep.z);
                    continue;
                }

                var t = bones.Get((Joint)i);
                if (t == null)
                {
                    w.Write((byte)0);
                    w.Write(0f); w.Write(0f); w.Write(0f);
                    continue;
                }

                var p = root.InverseTransformPoint(t.position);
                w.Write((byte)1);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
            }

            var face = _face.Read(chara, bones);
            byte flags = 0;
            if (face.HasPatterns) flags |= Wire.FaceHasPatterns;
            if (face.HasEyeOpen) flags |= Wire.FaceHasEyeOpen;
            if (face.HasMouthOpen) flags |= Wire.FaceHasMouthOpen;
            if (face.HasGaze) flags |= Wire.FaceHasGaze;
            w.Write(flags);
            w.Write(face.EyesPattern);
            w.Write(face.MouthPattern);
            w.Write(face.EyebrowPattern);
            w.Write(face.EyesOpenMax);
            w.Write(face.EyeOpen);
            w.Write(face.MouthOpen);
            var g = face.HasGaze ? root.InverseTransformDirection(face.GazeWorld) : Vector3.forward;
            w.Write(g.x); w.Write(g.y); w.Write(g.z);

            var nameBytes = Utf8Prefix(SafeName(chara), 120);
            w.Write((ushort)nameBytes.Length);
            w.Write(nameBytes);

            // Face regions (optional block).
            if (_sendFace.Value && state.Face.IsReady)
            {
                var disp = state.Face.Sample();
                var pos = state.Face.RegionPos;
                var valid = state.Face.RegionValid;
                w.Write((byte)FaceRegion.Count);
                for (int k = 0; k < (int)FaceRegion.Count; k++)
                {
                    w.Write((byte)(valid[k] ? 1 : 0));
                    w.Write(pos[k].x); w.Write(pos[k].y); w.Write(pos[k].z);
                    w.Write(disp[k].x); w.Write(disp[k].y); w.Write(disp[k].z);
                }
            }
            else
            {
                w.Write((byte)0);
            }

            // Sender state, so Limber in FFXIV can show Pause or Resume.
            w.Write((byte)(_pauser.Paused ? Wire.SenderAnimPaused : 0));

            w.Flush();
            return (int)_buffer.Position;
        }

        /// <summary>
        /// Track where each breast rests when not bouncing: an exponential average of its direction in
        /// the chest frame. Bounce oscillates around this; slow pose changes are followed within ~1 s.
        /// </summary>
        private static void UpdateBustRest(ChannelState state)
        {
            var spine = state.Bones.Get(Joint.Spine3);
            if (spine == null)
                return;
            float now = Time.unscaledTime;
            float dt = state.LastSampleTime > 0f ? Mathf.Clamp(now - state.LastSampleTime, 0f, 0.25f) : 0f;
            state.LastSampleTime = now;
            float a = dt > 0f ? 1f - Mathf.Exp(-dt / 0.6f) : 1f;

            for (int side = 0; side < 2; side++)
            {
                var bRoot = state.Bones.Get(side == 0 ? Joint.BustRootL : Joint.BustRootR);
                var bTip = state.Bones.Get(side == 0 ? Joint.BustTipL : Joint.BustTipR);
                if (bRoot == null || bTip == null)
                {
                    state.HasBustRest[side] = false;
                    continue;
                }
                var dir = bTip.position - bRoot.position;
                if (dir.sqrMagnitude < 1e-10f)
                    continue;
                var local = spine.InverseTransformDirection(dir.normalized);
                if (!state.HasBustRest[side])
                {
                    state.BustRest[side] = local;
                    state.HasBustRest[side] = true;
                }
                else
                {
                    state.BustRest[side] = Vector3.Slerp(state.BustRest[side], local, a).normalized;
                }
            }
        }

        private readonly Vector3[] _danPoints = new Vector3[DanPointCount];
        private readonly List<Vector3> _chainScratch = new List<Vector3>();
        private const int DanPointCount = (int)Joint.DanP6 - (int)Joint.DanP0 + 1;

        /// <summary>
        /// Resample a bone chain (world positions, base to tip) into evenly spaced points, so the FFXIV
        /// chain gets the same curve whatever the number of Koikatsu segments.
        /// </summary>
        private bool ResampleChain(List<Transform> chain, Vector3[] output)
        {
            _chainScratch.Clear();
            for (int i = 0; i < chain.Count; i++)
                if (chain[i] != null)
                    _chainScratch.Add(chain[i].position);
            if (_chainScratch.Count < 2)
                return false;

            float total = 0f;
            for (int i = 1; i < _chainScratch.Count; i++)
                total += Vector3.Distance(_chainScratch[i - 1], _chainScratch[i]);
            if (total < 1e-5f)
                return false;

            int seg = 1;
            float segStart = 0f;
            for (int k = 0; k < output.Length; k++)
            {
                float target = total * k / (output.Length - 1);
                while (seg < _chainScratch.Count - 1 &&
                       segStart + Vector3.Distance(_chainScratch[seg - 1], _chainScratch[seg]) < target)
                {
                    segStart += Vector3.Distance(_chainScratch[seg - 1], _chainScratch[seg]);
                    seg++;
                }
                var a = _chainScratch[seg - 1];
                var b = _chainScratch[seg];
                float len = Vector3.Distance(a, b);
                float f = len > 1e-6f ? Mathf.Clamp01((target - segStart) / len) : 0f;
                output[k] = Vector3.Lerp(a, b, f);
            }
            return true;
        }

        /// <summary>UTF-8 bytes of s, cut at a character boundary so the name never ends in a broken character.</summary>
        private static byte[] Utf8Prefix(string s, int maxBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= maxBytes)
                return bytes;
            int len = maxBytes;
            while (len > 0 && (bytes[len] & 0xC0) == 0x80) // don't split a multi-byte sequence
                len--;
            var result = new byte[len];
            Array.Copy(bytes, result, len);
            return result;
        }

        // ------------------------------------------------------------------------------------
        // Channel window: every channel, which FFXIV actor it drives, and which Koikatsu character feeds it.

        private void DrawChannelWindow(int id)
        {
            var links = _ffxivLinks;
            int age = Environment.TickCount - (int)System.Threading.Interlocked.Read(ref _ffxivLinksAt);
            bool connected = _ffxivLinksAt != 0 && age >= 0 && age < 3000;
            GUILayout.Label(connected
                ? "FFXIV is connected. Assign a Koikatsu character to each channel it uses."
                : "Waiting for FFXIV (start the game with Limber loaded).");

            bool manual = AnyLinked();
            _windowScroll = GUILayout.BeginScrollView(_windowScroll);
            for (int ch = 0; ch < Wire.MaxChannels; ch++)
            {
                var ffxiv = new List<string>();
                bool active = false;
                foreach (var l in links)
                {
                    if (l.Channel != ch)
                        continue;
                    ffxiv.Add(l.Name);
                    active |= l.Active;
                }
                string kk = _linked[ch] != null ? SafeName(_linked[ch])
                    : !manual && ch == 0 ? "(the selected character)" : "nobody";

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("Channel " + (ch + 1) + "   FFXIV: " +
                                (ffxiv.Count == 0 ? "not used" : string.Join(", ", ffxiv.ToArray()) + (active ? "" : " (off / not in scene)")));
                GUILayout.BeginHorizontal();
                GUILayout.Label("Koikatsu: " + kk, GUILayout.Width(230));
                if (GUILayout.Button("Selected", GUILayout.Width(70)))
                    AssignToChannel(ch, SelectedCharacter());
                if (GUILayout.Button(_pickingFor == ch ? "Close" : "Pick...", GUILayout.Width(60)))
                    _pickingFor = _pickingFor == ch ? -1 : ch;
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                    _linked[ch] = null;
                GUILayout.EndHorizontal();
                if (_pickingFor == ch)
                {
                    foreach (var c in AllCharacters())
                    {
                        if (GUILayout.Button(SafeName(c)))
                        {
                            AssignToChannel(ch, c);
                            _pickingFor = -1;
                        }
                    }
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Link all (scene order)"))
                LinkAll();
            if (GUILayout.Button("Clear all"))
                ClearLinks();
            if (GUILayout.Button("Close"))
                _showWindow = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        // A character onto one channel (taken off any other channel it was on).
        private void AssignToChannel(int channel, ChaControl chara)
        {
            if (chara == null)
            {
                Logger.LogMessage("Limber: select a character first");
                return;
            }
            for (int i = 0; i < _linked.Length; i++)
                if (_linked[i] == chara)
                    _linked[i] = null;
            _linked[channel] = chara;
            Logger.LogMessage("Limber: " + SafeName(chara) + " -> channel " + (channel + 1));
        }

        private static string SafeName(ChaControl c)
        {
            try
            {
                var n = c.chaFile != null && c.chaFile.parameter != null ? c.chaFile.parameter.fullname : null;
                return string.IsNullOrEmpty(n) ? c.name : n.Trim();
            }
            catch
            {
                return c != null ? c.name : "?";
            }
        }

        private void OnGUI()
        {
            if (_showWindow)
                _windowRect = GUILayout.Window(0x4C424D57, _windowRect, DrawChannelWindow, "Limber: FFXIV channels (Ctrl+F12)");

            if (!_showStatus.Value)
                return;

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, wordWrap = false };
            }

            _overlay.Length = 0;
            _overlay.Append("Limber: ").Append(_status).Append("  |  Ctrl+F12: channels");
            if (_pauser.Paused)
                _overlay.Append("  |  animation paused (Ctrl+F8)");
            for (int i = 0; i < _channels.Length; i++)
            {
                if (string.IsNullOrEmpty(_channels[i].Info))
                    continue;
                _overlay.Append('\n').Append("Ch ").Append(i + 1).Append(": ").Append(_channels[i].Info);
            }

            // Bottom-right corner, with a 1px shadow so it stays readable on bright scenes.
            const float width = 700f, height = 200f, margin = 8f;
            var rect = new Rect(Screen.width - width - margin, Screen.height - height - margin, width, height);
            var text = _overlay.ToString();
            var color = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _overlayStyle);
            GUI.color = color;
            GUI.Label(rect, text, _overlayStyle);
        }
    }
}
