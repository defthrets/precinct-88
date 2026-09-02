using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using Precinct88.Core;

namespace Precinct88.UI
{
    /// <summary>
    /// What an officer is actually saying to you, and what you can say back.
    ///
    /// EVERY POLICE SCENE IN THIS MOD HAS BEEN MIME UNTIL NOW. An officer walked to your window
    /// and stood there for five seconds, and then a notification in the corner told you what had
    /// been decided -- so the officer was scenery and the ticker was the mod. The whole point of
    /// making him walk over was that the encounter happens between two people, and it cannot do
    /// that while one of them never speaks.
    ///
    /// A LINE ON SCREEN IS NOT A NOTIFICATION AND MUST NOT LOOK LIKE ONE. Notifications are what
    /// this mod uses for things it is telling YOU, out of band: a fine was taken, a call went
    /// out. This is a man stood in front of you talking, so it is anchored low and centre where
    /// a conversation belongs, it names who is speaking, and it stays up until it is answered
    /// rather than sliding away on a timer.
    ///
    /// THE QUESTIONS ARE REAL QUESTIONS. Ask() does not resolve until the player presses
    /// something or the officer gives up waiting, and the scene that asked gets the answer. That
    /// is the difference between dialogue and a subtitle.
    ///
    /// STATIC, LIKE SCREEN, and for the same reason: there is one player, one officer talking to
    /// him at a time, and threading an instance through every scene that might want to say
    /// something would be ceremony around a single global fact. Clear() exists because static
    /// state has to be resettable when the mod unloads.
    /// </summary>
    internal static class Dialogue
    {
        /// <summary>Where the box sits. Low and centred -- a conversation, not a HUD.</summary>
        private const float BoxY = 0.795f;
        private const float BoxW = 0.50f;

        /// <summary>How tall one line of body text is, in the same space.</summary>
        private const float LineH = 0.030f;

        /// <summary>Padding inside the box, top and bottom.</summary>
        private const float PadY = 0.028f;

        /// <summary>Roughly how many characters fit on a line at the body scale.</summary>
        private const int Wrap = 52;

        /// <summary>How long an officer waits for an answer before giving up on you.</summary>
        private const int AnswerMs = 9000;

        private static readonly Color Back = Color.FromArgb(214, 8, 9, 12);
        private static readonly Color Edge = Color.FromArgb(235, 74, 122, 196);
        private static readonly Color Who = Color.FromArgb(255, 126, 168, 232);
        private static readonly Color Body = Color.FromArgb(255, 236, 238, 242);
        private static readonly Color Key = Color.FromArgb(255, 250, 206, 106);

        private static string _who;
        private static List<string> _lines;
        private static int _untilAt;

        private static string _optionA;
        private static string _optionB;
        private static Action<bool> _answered;

        /// <summary>Whether a question is up and the scene should wait for it.</summary>
        public static bool Waiting => _answered != null;

        /// <summary>Whether anything at all is on screen.</summary>
        public static bool Showing => _lines != null;

        /// <summary>
        /// Somebody says something. Auto-dismisses.
        /// </summary>
        public static void Say(string who, string line, int ms = 4200)
        {
            // A STATEMENT NEVER REPLACES A QUESTION. Scenes drive this from a per-tick loop and
            // a stray Say on the frame after an Ask would drop the question, discard the
            // callback, and leave whatever asked it waiting for an answer that can never come.
            if (Waiting) return;

            _who = who;
            _lines = Split(line);
            _untilAt = Game.GameTime + ms;

            _optionA = null;
            _optionB = null;
            _answered = null;
        }

        /// <summary>
        /// Somebody asks something, and waits.
        ///
        /// <paramref name="answered"/> is called exactly once: with true for the first option,
        /// false for the second, and false if the officer gets bored of waiting. Silence being
        /// the same as the second option is deliberate -- the second option is always the
        /// unhelpful one, and saying nothing to a police officer is not the helpful choice.
        /// </summary>
        public static void Ask(string who, string question, string optionA, string optionB,
                               Action<bool> answered)
        {
            _who = who;
            _lines = Split(question);
            _untilAt = Game.GameTime + AnswerMs;

            _optionA = optionA;
            _optionB = optionB;
            _answered = answered;
        }

        /// <summary>
        /// Reads the answer, and times the question out. Called every frame by Main.
        /// </summary>
        public static void Update()
        {
            if (_lines == null) return;

            try
            {
                if (Waiting)
                {
                    if (Game.IsControlJustPressed(GTA.Control.Context)) { Answer(true); return; }
                    if (Game.IsControlJustPressed(GTA.Control.Detonate)) { Answer(false); return; }

                    // He gave up on you. Counts as the unhelpful answer, which it is.
                    if (Game.GameTime > _untilAt) Answer(false);

                    return;
                }

                if (Game.GameTime > _untilAt) Clear();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read a dialogue answer: " + ex.Message);
                Clear();
            }
        }

        private static void Answer(bool first)
        {
            var handler = _answered;

            // CLEARED BEFORE THE CALLBACK, not after. The scene being told the answer will very
            // likely say something next, and if the question were still up that Say would be
            // swallowed by the guard at the top of it.
            _answered = null;
            _optionA = null;
            _optionB = null;
            _lines = null;

            if (handler == null) return;

            try
            {
                handler(first);
            }
            catch (Exception ex)
            {
                Log.Debug("A dialogue answer handler threw: " + ex.Message);
            }
        }

        /// <summary>Draws it. Called every frame by Main, after everything else.</summary>
        public static void Draw()
        {
            if (_lines == null) return;

            try
            {
                var rows = _lines.Count;
                var tall = PadY * 2f + LineH * rows + (Waiting ? LineH * 1.15f : 0f);

                var top = BoxY - tall * 0.5f;

                // A thin bar down the left rather than a border all the way round: it reads as
                // somebody speaking rather than as a dialog box asking you to click OK.
                Screen.Rect(0.5f, BoxY, BoxW, tall, Back);
                Screen.Rect(0.5f - BoxW * 0.5f + 0.0018f, BoxY, 0.0036f, tall, Edge);

                var left = 0.5f - BoxW * 0.5f + 0.016f;
                var y = top + PadY * 0.42f;

                Screen.Text(_who ?? "Officer", left, y, 0.26f, Who);

                y += LineH * 0.72f;

                for (var i = 0; i < rows; i++)
                {
                    Screen.Text(_lines[i], left, y, 0.36f, Body);
                    y += LineH;
                }

                if (!Waiting) return;

                // The two answers, on one line, with the keys called out. Not a menu -- there is
                // no cursor and nothing to scroll, because an officer asking you a yes or no
                // question is not a shop.
                Screen.Text("[E] " + _optionA, left, y + LineH * 0.12f, 0.32f, Key);

                Screen.Text("[G] " + _optionB, left + BoxW * 0.46f, y + LineH * 0.12f, 0.32f, Key);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the dialogue: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes everything down.
        ///
        /// ANY PENDING QUESTION IS ANSWERED, not dropped. A scene waiting on a callback that
        /// never arrives is a scene that never ends -- an officer stood at a window forever
        /// because the mod unloaded mid-sentence.
        /// </summary>
        public static void Clear()
        {
            var handler = _answered;

            _who = null;
            _lines = null;
            _untilAt = 0;
            _optionA = null;
            _optionB = null;
            _answered = null;

            if (handler == null) return;

            try { handler(false); }
            catch { /* Teardown. Nothing left to tell. */ }
        }

        /// <summary>
        /// Breaks a line into ones that fit, on word boundaries.
        ///
        /// BY HAND RATHER THAN WITH SET_TEXT_WRAP. The game's wrap works, but it takes a window
        /// in screen space and interacts badly with the centring and right-justify flags that
        /// Screen.Text already sets -- and when it goes wrong it does not wrap, it draws
        /// nothing at all. Counting characters is cruder and cannot fail silently.
        /// </summary>
        private static List<string> Split(string line)
        {
            var rows = new List<string>();

            if (string.IsNullOrEmpty(line)) return rows;

            var words = line.Split(' ');
            var row = string.Empty;

            foreach (var word in words)
            {
                if (row.Length == 0)
                {
                    row = word;
                    continue;
                }

                if (row.Length + 1 + word.Length <= Wrap)
                {
                    row = row + " " + word;
                    continue;
                }

                rows.Add(row);
                row = word;
            }

            if (row.Length > 0) rows.Add(row);

            return rows;
        }
    }
}
