using System.Collections.Generic;
using UnityEngine;

namespace Limber.Koikatsu
{
    /// <summary>
    /// Pauses and resumes the body animation of every character in the scene by holding each character's
    /// Animator at speed 0, and gives every one its own speed back on resume. Things that set the speed every
    /// frame (H scenes, Studio's speed slider) are overridden for as long as the pause lasts, and their latest
    /// speed is the one restored. Blinking, physics and the camera keep running.
    /// </summary>
    internal sealed class AnimationPauser
    {
        private readonly Dictionary<Animator, float> _saved = new Dictionary<Animator, float>();
        private readonly List<Animator> _targets = new List<Animator>();
        private float _nextScan;

        public bool Paused { get; private set; }

        public void SetPaused(bool paused)
        {
            if (paused == Paused)
                return;
            Paused = paused;
            if (paused)
            {
                _nextScan = 0f;
                Enforce();
            }
            else
            {
                Restore();
            }
        }

        /// <summary>Call every frame after other scripts' Update and before animation (a coroutine's yield null).</summary>
        public void Enforce()
        {
            if (!Paused)
                return;

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                Rescan();
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                var a = _targets[i];
                if (a == null) // Unity null: character removed
                    continue;
                if (a.speed != 0f)
                {
                    _saved[a] = a.speed;
                    a.speed = 0f;
                }
            }
        }

        private void Rescan()
        {
            _targets.Clear();
            foreach (var c in Object.FindObjectsOfType<ChaControl>())
            {
                if (c == null || c.animBody == null)
                    continue;
                _targets.Add(c.animBody);
            }
        }

        private void Restore()
        {
            foreach (var kv in _saved)
            {
                if (kv.Key != null)
                    kv.Key.speed = kv.Value;
            }
            _saved.Clear();
            _targets.Clear();
        }
    }
}
