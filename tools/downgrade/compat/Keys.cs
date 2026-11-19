using System.Collections.Generic;
using UnityEngine;

// The keyboard for InControl's KeyBindingSource (InControl.cs) on the Windows test players: InControl's Key values,
// each with the name InControl gives it and the Unity KeyCodes it reads. The game turns binding names into glyphs
// (MainMenu.parseBindingButtonsForAction compares their upper-case form with "UP ARROW", "LEFT SHIFT", "PAD ENTER",
// ...), so the names are InControl's own (UnityKeyboardProvider, its Windows names). Unity 2018.2's KeyCode has no
// LeftMeta and RightMeta (Command reads LeftCommand and RightCommand) and no Tilde, Percent, curly brackets or Pipe
// (those keys read nothing). The PS Vita has no keyboard:
// Input.GetKey answers false there, and the keyboard listener the game creates reads nothing.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Keys
    {
        sealed class Entry
        {
            public readonly string Name;
            public readonly KeyCode[] Codes;

            public Entry(string name, params KeyCode[] codes)
            {
                Name = name;
                Codes = codes;
            }
        }

        // Modifier keys, which a listener takes as keys of their own only when asked to
        // (BindingListenOptions.IncludeModifiersAsFirstClassKeys).
        static readonly HashSet<InControl_Key> Modifiers = new HashSet<InControl_Key>
        {
            InControl_Key.Shift, InControl_Key.Alt, InControl_Key.Command, InControl_Key.Control,
            InControl_Key.LeftShift, InControl_Key.LeftAlt, InControl_Key.LeftCommand, InControl_Key.LeftControl,
            InControl_Key.RightShift, InControl_Key.RightAlt, InControl_Key.RightCommand, InControl_Key.RightControl,
        };

        static readonly Dictionary<InControl_Key, Entry> table = new Dictionary<InControl_Key, Entry>
        {
            { InControl_Key.None, new Entry("None", KeyCode.None) },
            { InControl_Key.Shift, new Entry("Shift", KeyCode.LeftShift, KeyCode.RightShift) },
            { InControl_Key.Alt, new Entry("Alt", KeyCode.LeftAlt, KeyCode.RightAlt) },
            { InControl_Key.Command, new Entry("Command", KeyCode.LeftCommand, KeyCode.RightCommand) },
            { InControl_Key.Control, new Entry("Control", KeyCode.LeftControl, KeyCode.RightControl) },
            { InControl_Key.LeftShift, new Entry("Left Shift", KeyCode.LeftShift) },
            { InControl_Key.LeftAlt, new Entry("Left Alt", KeyCode.LeftAlt) },
            { InControl_Key.LeftCommand, new Entry("Left Command", KeyCode.LeftCommand) },
            { InControl_Key.LeftControl, new Entry("Left Control", KeyCode.LeftControl) },
            { InControl_Key.RightShift, new Entry("Right Shift", KeyCode.RightShift) },
            { InControl_Key.RightAlt, new Entry("Right Alt", KeyCode.RightAlt) },
            { InControl_Key.RightCommand, new Entry("Right Command", KeyCode.RightCommand) },
            { InControl_Key.RightControl, new Entry("Right Control", KeyCode.RightControl) },
            { InControl_Key.Escape, new Entry("Escape", KeyCode.Escape) },
            { InControl_Key.F1, new Entry("F1", KeyCode.F1) },
            { InControl_Key.F2, new Entry("F2", KeyCode.F2) },
            { InControl_Key.F3, new Entry("F3", KeyCode.F3) },
            { InControl_Key.F4, new Entry("F4", KeyCode.F4) },
            { InControl_Key.F5, new Entry("F5", KeyCode.F5) },
            { InControl_Key.F6, new Entry("F6", KeyCode.F6) },
            { InControl_Key.F7, new Entry("F7", KeyCode.F7) },
            { InControl_Key.F8, new Entry("F8", KeyCode.F8) },
            { InControl_Key.F9, new Entry("F9", KeyCode.F9) },
            { InControl_Key.F10, new Entry("F10", KeyCode.F10) },
            { InControl_Key.F11, new Entry("F11", KeyCode.F11) },
            { InControl_Key.F12, new Entry("F12", KeyCode.F12) },
            { InControl_Key.Key0, new Entry("Num 0", KeyCode.Alpha0) },
            { InControl_Key.Key1, new Entry("Num 1", KeyCode.Alpha1) },
            { InControl_Key.Key2, new Entry("Num 2", KeyCode.Alpha2) },
            { InControl_Key.Key3, new Entry("Num 3", KeyCode.Alpha3) },
            { InControl_Key.Key4, new Entry("Num 4", KeyCode.Alpha4) },
            { InControl_Key.Key5, new Entry("Num 5", KeyCode.Alpha5) },
            { InControl_Key.Key6, new Entry("Num 6", KeyCode.Alpha6) },
            { InControl_Key.Key7, new Entry("Num 7", KeyCode.Alpha7) },
            { InControl_Key.Key8, new Entry("Num 8", KeyCode.Alpha8) },
            { InControl_Key.Key9, new Entry("Num 9", KeyCode.Alpha9) },
            { InControl_Key.A, new Entry("A", KeyCode.A) },
            { InControl_Key.B, new Entry("B", KeyCode.B) },
            { InControl_Key.C, new Entry("C", KeyCode.C) },
            { InControl_Key.D, new Entry("D", KeyCode.D) },
            { InControl_Key.E, new Entry("E", KeyCode.E) },
            { InControl_Key.F, new Entry("F", KeyCode.F) },
            { InControl_Key.G, new Entry("G", KeyCode.G) },
            { InControl_Key.H, new Entry("H", KeyCode.H) },
            { InControl_Key.I, new Entry("I", KeyCode.I) },
            { InControl_Key.J, new Entry("J", KeyCode.J) },
            { InControl_Key.K, new Entry("K", KeyCode.K) },
            { InControl_Key.L, new Entry("L", KeyCode.L) },
            { InControl_Key.M, new Entry("M", KeyCode.M) },
            { InControl_Key.N, new Entry("N", KeyCode.N) },
            { InControl_Key.O, new Entry("O", KeyCode.O) },
            { InControl_Key.P, new Entry("P", KeyCode.P) },
            { InControl_Key.Q, new Entry("Q", KeyCode.Q) },
            { InControl_Key.R, new Entry("R", KeyCode.R) },
            { InControl_Key.S, new Entry("S", KeyCode.S) },
            { InControl_Key.T, new Entry("T", KeyCode.T) },
            { InControl_Key.U, new Entry("U", KeyCode.U) },
            { InControl_Key.V, new Entry("V", KeyCode.V) },
            { InControl_Key.W, new Entry("W", KeyCode.W) },
            { InControl_Key.X, new Entry("X", KeyCode.X) },
            { InControl_Key.Y, new Entry("Y", KeyCode.Y) },
            { InControl_Key.Z, new Entry("Z", KeyCode.Z) },
            { InControl_Key.Backquote, new Entry("Backquote", KeyCode.BackQuote) },
            { InControl_Key.Minus, new Entry("Minus", KeyCode.Minus) },
            { InControl_Key.Equals, new Entry("Equals", KeyCode.Equals) },
            { InControl_Key.Backspace, new Entry("Backspace", KeyCode.Backspace) },
            { InControl_Key.Tab, new Entry("Tab", KeyCode.Tab) },
            { InControl_Key.LeftBracket, new Entry("Left Bracket", KeyCode.LeftBracket) },
            { InControl_Key.RightBracket, new Entry("Right Bracket", KeyCode.RightBracket) },
            { InControl_Key.Backslash, new Entry("Backslash", KeyCode.Backslash) },
            { InControl_Key.Semicolon, new Entry("Semicolon", KeyCode.Semicolon) },
            { InControl_Key.Quote, new Entry("Quote", KeyCode.Quote) },
            { InControl_Key.Return, new Entry("Return", KeyCode.Return) },
            { InControl_Key.Comma, new Entry("Comma", KeyCode.Comma) },
            { InControl_Key.Period, new Entry("Period", KeyCode.Period) },
            { InControl_Key.Slash, new Entry("Slash", KeyCode.Slash) },
            { InControl_Key.Space, new Entry("Space", KeyCode.Space) },
            { InControl_Key.Insert, new Entry("Insert", KeyCode.Insert) },
            { InControl_Key.Delete, new Entry("Delete", KeyCode.Delete) },
            { InControl_Key.Home, new Entry("Home", KeyCode.Home) },
            { InControl_Key.End, new Entry("End", KeyCode.End) },
            { InControl_Key.PageUp, new Entry("PageUp", KeyCode.PageUp) },
            { InControl_Key.PageDown, new Entry("PageDown", KeyCode.PageDown) },
            { InControl_Key.LeftArrow, new Entry("Left Arrow", KeyCode.LeftArrow) },
            { InControl_Key.RightArrow, new Entry("Right Arrow", KeyCode.RightArrow) },
            { InControl_Key.UpArrow, new Entry("Up Arrow", KeyCode.UpArrow) },
            { InControl_Key.DownArrow, new Entry("Down Arrow", KeyCode.DownArrow) },
            { InControl_Key.Pad0, new Entry("Pad 0", KeyCode.Keypad0) },
            { InControl_Key.Pad1, new Entry("Pad 1", KeyCode.Keypad1) },
            { InControl_Key.Pad2, new Entry("Pad 2", KeyCode.Keypad2) },
            { InControl_Key.Pad3, new Entry("Pad 3", KeyCode.Keypad3) },
            { InControl_Key.Pad4, new Entry("Pad 4", KeyCode.Keypad4) },
            { InControl_Key.Pad5, new Entry("Pad 5", KeyCode.Keypad5) },
            { InControl_Key.Pad6, new Entry("Pad 6", KeyCode.Keypad6) },
            { InControl_Key.Pad7, new Entry("Pad 7", KeyCode.Keypad7) },
            { InControl_Key.Pad8, new Entry("Pad 8", KeyCode.Keypad8) },
            { InControl_Key.Pad9, new Entry("Pad 9", KeyCode.Keypad9) },
            { InControl_Key.Numlock, new Entry("Numlock", KeyCode.Numlock) },
            { InControl_Key.PadDivide, new Entry("Pad Divide", KeyCode.KeypadDivide) },
            { InControl_Key.PadMultiply, new Entry("Pad Multiply", KeyCode.KeypadMultiply) },
            { InControl_Key.PadMinus, new Entry("Pad Minus", KeyCode.KeypadMinus) },
            { InControl_Key.PadPlus, new Entry("Pad Plus", KeyCode.KeypadPlus) },
            { InControl_Key.PadEnter, new Entry("Pad Enter", KeyCode.KeypadEnter) },
            { InControl_Key.PadPeriod, new Entry("Pad Period", KeyCode.KeypadPeriod) },
            { InControl_Key.Clear, new Entry("Clear", KeyCode.Clear) },
            { InControl_Key.PadEquals, new Entry("Pad Equals", KeyCode.KeypadEquals) },
            { InControl_Key.F13, new Entry("F13", KeyCode.F13) },
            { InControl_Key.F14, new Entry("F14", KeyCode.F14) },
            { InControl_Key.F15, new Entry("F15", KeyCode.F15) },
            { InControl_Key.AltGr, new Entry("Alt Graphic", KeyCode.AltGr) },
            { InControl_Key.CapsLock, new Entry("Caps Lock", KeyCode.CapsLock) },
            { InControl_Key.ExclamationMark, new Entry("Exclamation", KeyCode.Exclaim) },
            { InControl_Key.Tilde, new Entry("Tilde", KeyCode.None) },
            { InControl_Key.At, new Entry("At", KeyCode.At) },
            { InControl_Key.Hash, new Entry("Hash", KeyCode.Hash) },
            { InControl_Key.Dollar, new Entry("Dollar", KeyCode.Dollar) },
            { InControl_Key.Percent, new Entry("Percent", KeyCode.None) },
            { InControl_Key.Caret, new Entry("Caret", KeyCode.Caret) },
            { InControl_Key.Ampersand, new Entry("Ampersand", KeyCode.Ampersand) },
            { InControl_Key.Asterisk, new Entry("Asterisk", KeyCode.Asterisk) },
            { InControl_Key.LeftParen, new Entry("Left Paren", KeyCode.LeftParen) },
            { InControl_Key.RightParen, new Entry("Right Paren", KeyCode.RightParen) },
            { InControl_Key.Underscore, new Entry("Underscore", KeyCode.Underscore) },
            { InControl_Key.Plus, new Entry("Plus", KeyCode.Plus) },
            { InControl_Key.LeftBrace, new Entry("LeftBrace", KeyCode.None) },
            { InControl_Key.RightBrace, new Entry("RightBrace", KeyCode.None) },
            { InControl_Key.Pipe, new Entry("Pipe", KeyCode.None) },
            { InControl_Key.Colon, new Entry("Colon", KeyCode.Colon) },
            { InControl_Key.DoubleQuote, new Entry("Double Quote", KeyCode.DoubleQuote) },
            { InControl_Key.LessThan, new Entry("Less Than", KeyCode.Less) },
            { InControl_Key.GreaterThan, new Entry("Greater Than", KeyCode.Greater) },
            { InControl_Key.QuestionMark, new Entry("Question Mark", KeyCode.Question) },
        };

        static Entry Find(InControl_Key key)
        {
            Entry entry;
            return table.TryGetValue(key, out entry) ? entry : null;
        }

        public static string Name(InControl_Key key)
        {
            Entry entry = Find(key);
            return entry == null ? key.ToString() : entry.Name;
        }

        public static bool IsHeld(InControl_Key key)
        {
            Entry entry = Find(key);
            if (entry == null || key == InControl_Key.None) return false;
            foreach (KeyCode code in entry.Codes)
                if (Input.GetKey(code)) return true;
            return false;
        }

        // The key that went down since the last tick, for rebinding; None when there is none, and on the first tick asked
        // (a key held when the listening started, such as the one that chose the menu item, doesn't count). By tick, not
        // Input.GetKeyDown: the game updates its input from FixedUpdate, which runs zero or more times a frame. The
        // combined modifiers (Shift, Alt, ...) are never offered: their left and right keys are.
        static HashSet<InControl_Key> held = new HashSet<InControl_Key>();
        static ulong heldTick;

        public static InControl_Key PressedNow(ulong tick, bool modifiersAsKeys)
        {
            var now = new HashSet<InControl_Key>();
            InControl_Key found = InControl_Key.None;
            foreach (KeyValuePair<InControl_Key, Entry> pair in table)
            {
                InControl_Key key = pair.Key;
                if (key == InControl_Key.None || key == InControl_Key.Shift || key == InControl_Key.Alt ||
                    key == InControl_Key.Command || key == InControl_Key.Control)
                    continue;
                if (!modifiersAsKeys && Modifiers.Contains(key)) continue;
                if (!IsHeld(key)) continue;
                now.Add(key);
                if (found == InControl_Key.None && !held.Contains(key)) found = key;
            }
            bool consecutive = heldTick + 1 == tick;
            held = now;
            heldTick = tick;
            return consecutive ? found : InControl_Key.None;
        }

        // A one-key array, for the game's keyboard bindings (new Key[] { key }): repairs of the game's
        // PlayerActions..ctor and CreateWithKeyboardBindings can pass Rewind's constant here.
        public static InControl_Key[] One(int key)
        {
            return new[] { (InControl_Key)key };
        }
    }
}
