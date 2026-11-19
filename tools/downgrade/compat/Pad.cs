using System;
using System.Collections.Generic;
using UnityEngine;

// The pads behind InControl's InputDevice (InControl.cs), read through Unity's Input: buttons as KeyCode
// Joystick<n>Button<b>, axes by the names InControl reads, "joystick <n> analog <a>", which the game's
// InputManager.asset defines for joysticks 1-4 and axes 0-7 (the downgraded project must keep them).
//
// Two layouts:
// - The PS Vita (Unity's PSP2 player), joystick 1, always attached: Cross, Circle, Square, Triangle on buttons 0-3,
//   L and R on 4 and 5, Select 6, Start 7; the left stick on axes 0 and 1, the right on 3 and 4 (up negative), the
//   d-pad on axes 5 and 6 (right and up positive). InControl's own PS Vita profile reads the pad the same way, and
//   butcher-vita's port read it so on the console. The pad has no triggers and no bumpers: L and R are the triggers,
//   which the game's Aim action (PlayerActions.LeftTrig) is bound to, so they aim as the triggers do on PC. The
//   bumpers, which the game binds to Interact besides Circle, aren't reported, and the default bindings to them are
//   left out (PlayerAction.AddDefaultBinding asks Offers), so the controls menu doesn't show them.
//   Joysticks 2-4 are read too when Unity names them (a PS TV's pads), in the same layout.
// - Elsewhere (the Windows test players), each joystick Unity names, in the layout Unity gives an XInput pad: A, B,
//   X, Y on buttons 0-3, the bumpers 4 and 5, View 6, Menu 7, the stick clicks 8 and 9, the sticks and d-pad on the
//   same axes as the PS Vita, the triggers on axes 8 and 9 if the project's InputManager has them, else on axis 2
//   (left positive, right negative). Other pads are read as if they were XInput pads.
//
// The names are InControl's (its profiles' control names), which the game turns into glyphs
// (MainMenu.parseBindingButtonsForAction): on the PS Vita, L and R are "L1" and "R1", the PlayStation glyphs
// closest to the PS Vita's L and R; the device style is PlayStationVita, which the game shows with its PS4 glyphs.
//   -t2d-input-log    log each button and axis change of every pad (T2D_INPUT), for checking the layout on a console
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Pad
    {
        // Axes, as InControl's "analog" numbers.
        public const int LeftX = 0, LeftY = 1, Triggers = 2, RightX = 3, RightY = 4, DPadX = 5, DPadY = 6, LeftTriggerAxis = 8, RightTriggerAxis = 9;

        // The controls every device has (InputDevice's controls).
        public static readonly InControl_InputControlType[] Controls =
        {
            InControl_InputControlType.LeftStickUp, InControl_InputControlType.LeftStickDown, InControl_InputControlType.LeftStickLeft,
            InControl_InputControlType.LeftStickRight, InControl_InputControlType.LeftStickX, InControl_InputControlType.LeftStickY,
            InControl_InputControlType.RightStickUp, InControl_InputControlType.RightStickDown, InControl_InputControlType.RightStickLeft,
            InControl_InputControlType.RightStickRight, InControl_InputControlType.RightStickX, InControl_InputControlType.RightStickY,
            InControl_InputControlType.LeftStickButton, InControl_InputControlType.RightStickButton,
            InControl_InputControlType.DPadUp, InControl_InputControlType.DPadDown, InControl_InputControlType.DPadLeft, InControl_InputControlType.DPadRight,
            InControl_InputControlType.LeftTrigger, InControl_InputControlType.RightTrigger,
            InControl_InputControlType.LeftBumper, InControl_InputControlType.RightBumper,
            InControl_InputControlType.Action1, InControl_InputControlType.Action2, InControl_InputControlType.Action3, InControl_InputControlType.Action4,
            InControl_InputControlType.Back, InControl_InputControlType.Start, InControl_InputControlType.Select,
            InControl_InputControlType.View, InControl_InputControlType.Menu,
        };

        public static bool Vita
        {
            get { return Application.platform == RuntimePlatform.PSP2; }
        }

        // Buttons by layout: control -> Unity button number.
        static readonly Dictionary<int, int> VitaButtons = new Dictionary<int, int>
        {
            { (int)InControl_InputControlType.Action1, 0 }, { (int)InControl_InputControlType.Action2, 1 },
            { (int)InControl_InputControlType.Action3, 2 }, { (int)InControl_InputControlType.Action4, 3 },
            { (int)InControl_InputControlType.LeftTrigger, 4 }, { (int)InControl_InputControlType.RightTrigger, 5 },
            { (int)InControl_InputControlType.Select, 6 }, { (int)InControl_InputControlType.Start, 7 },
        };

        static readonly Dictionary<int, int> XInputButtons = new Dictionary<int, int>
        {
            { (int)InControl_InputControlType.Action1, 0 }, { (int)InControl_InputControlType.Action2, 1 },
            { (int)InControl_InputControlType.Action3, 2 }, { (int)InControl_InputControlType.Action4, 3 },
            { (int)InControl_InputControlType.LeftBumper, 4 }, { (int)InControl_InputControlType.RightBumper, 5 },
            { (int)InControl_InputControlType.View, 6 }, { (int)InControl_InputControlType.Menu, 7 },
            { (int)InControl_InputControlType.LeftStickButton, 8 }, { (int)InControl_InputControlType.RightStickButton, 9 },
        };

        static readonly Dictionary<int, string> VitaNames = new Dictionary<int, string>
        {
            { (int)InControl_InputControlType.Action1, "Cross" }, { (int)InControl_InputControlType.Action2, "Circle" },
            { (int)InControl_InputControlType.Action3, "Square" }, { (int)InControl_InputControlType.Action4, "Triangle" },
            { (int)InControl_InputControlType.LeftTrigger, "L1" }, { (int)InControl_InputControlType.RightTrigger, "R1" },
            { (int)InControl_InputControlType.Select, "Select" }, { (int)InControl_InputControlType.Start, "Start" },
        };

        static readonly Dictionary<int, string> XInputNames = new Dictionary<int, string>
        {
            { (int)InControl_InputControlType.Action1, "A" }, { (int)InControl_InputControlType.Action2, "B" },
            { (int)InControl_InputControlType.Action3, "X" }, { (int)InControl_InputControlType.Action4, "Y" },
            { (int)InControl_InputControlType.LeftBumper, "Left Bumper" }, { (int)InControl_InputControlType.RightBumper, "Right Bumper" },
            { (int)InControl_InputControlType.LeftTrigger, "Left Trigger" }, { (int)InControl_InputControlType.RightTrigger, "Right Trigger" },
            { (int)InControl_InputControlType.View, "View" }, { (int)InControl_InputControlType.Menu, "Menu" },
            { (int)InControl_InputControlType.LeftStickButton, "Left Stick Button" }, { (int)InControl_InputControlType.RightStickButton, "Right Stick Button" },
        };

        // Stick and d-pad names, as InControl's profiles give them (InputDeviceProfile's mappings).
        static readonly Dictionary<int, string> AxisNames = new Dictionary<int, string>
        {
            { (int)InControl_InputControlType.LeftStickUp, "Left Stick Up" }, { (int)InControl_InputControlType.LeftStickDown, "Left Stick Down" },
            { (int)InControl_InputControlType.LeftStickLeft, "Left Stick Left" }, { (int)InControl_InputControlType.LeftStickRight, "Left Stick Right" },
            { (int)InControl_InputControlType.RightStickUp, "Right Stick Up" }, { (int)InControl_InputControlType.RightStickDown, "Right Stick Down" },
            { (int)InControl_InputControlType.RightStickLeft, "Right Stick Left" }, { (int)InControl_InputControlType.RightStickRight, "Right Stick Right" },
            { (int)InControl_InputControlType.DPadUp, "DPad Up" }, { (int)InControl_InputControlType.DPadDown, "DPad Down" },
            { (int)InControl_InputControlType.DPadLeft, "DPad Left" }, { (int)InControl_InputControlType.DPadRight, "DPad Right" },
        };

        static Dictionary<int, int> Buttons { get { return Vita ? VitaButtons : XInputButtons; } }

        // Whether the platform's layout reports a control: the game's default bindings to others are left out.
        public static bool Offers(InControl_InputControlType control)
        {
            if (AxisNames.ContainsKey((int)control)) return true;
            if (!Vita && (control == InControl_InputControlType.LeftTrigger || control == InControl_InputControlType.RightTrigger)) return true;
            return Buttons.ContainsKey((int)control);
        }

        public static string ControlName(InControl_InputControlType control)
        {
            string name;
            if (AxisNames.TryGetValue((int)control, out name)) return name;
            if ((Vita ? VitaNames : XInputNames).TryGetValue((int)control, out name)) return name;
            return control.ToString();
        }

        public static bool IsStickDirection(InControl_InputControlType control)
        {
            return (control >= InControl_InputControlType.LeftStickUp && control <= InControl_InputControlType.LeftStickRight) ||
                   (control >= InControl_InputControlType.RightStickUp && control <= InControl_InputControlType.RightStickRight) ||
                   IsAxisPair(control);
        }

        // The signed axes (LeftStickX ...), which a rebinding never offers.
        public static bool IsAxisPair(InControl_InputControlType control)
        {
            return control >= InControl_InputControlType.LeftStickX && control <= InControl_InputControlType.DPadY;
        }

        static bool? inputLog;

        static bool InputLog
        {
            get
            {
                if (inputLog == null) inputLog = Flags.Has("-t2d-input-log");
                return inputLog.Value;
            }
        }

        public sealed class Reader
        {
            public readonly int Joystick;
            public string Name { get; internal set; }
            public readonly InControl_InputDeviceStyle Style;
            public readonly float StickDeadZone;
            readonly bool vita;
            readonly float[] axes = new float[10];
            readonly string[] axisNames = new string[10];
            readonly bool[] axisDefined = new bool[10];
            readonly int firstButton;
            bool separateTriggers;
            int polledFrame = -1;
            int[] logged;

            internal Reader(int joystick, string name, bool vita)
            {
                Joystick = joystick;
                Name = name;
                this.vita = vita;
                Style = vita ? InControl_InputDeviceStyle.PlayStationVita : InControl_InputDeviceStyle.XboxOne;
                // InControl's profiles: 0.25 for the PS Vita's sticks, 0.2 for the others.
                StickDeadZone = vita ? 0.25f : 0.2f;
                firstButton = (int)KeyCode.Joystick1Button0 + (joystick - 1) * 20;
                for (int a = 0; a < axes.Length; a++)
                {
                    axisNames[a] = "joystick " + joystick + " analog " + a;
                    axisDefined[a] = Defined(axisNames[a]);
                }
                separateTriggers = !vita && axisDefined[LeftTriggerAxis] && axisDefined[RightTriggerAxis];
            }

            static bool Defined(string axis)
            {
                try
                {
                    Input.GetAxisRaw(axis);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            // Reads the axes once a frame (the game updates its input from FixedUpdate, which can run more than once).
            internal void Poll()
            {
                if (polledFrame == Time.frameCount) return;
                polledFrame = Time.frameCount;
                for (int a = 0; a < axes.Length; a++)
                    axes[a] = axisDefined[a] ? Input.GetAxisRaw(axisNames[a]) : 0f;
                if (InputLog) Log();
            }

            // A stick's axis, up positive.
            internal float Axis(int axis)
            {
                float value = axes[axis];
                return axis == LeftY || axis == RightY ? -value : value;
            }

            internal float Control(InControl_InputControlType control)
            {
                switch (control)
                {
                    case InControl_InputControlType.DPadUp: return Math.Max(0f, axes[DPadY]);
                    case InControl_InputControlType.DPadDown: return Math.Max(0f, -axes[DPadY]);
                    case InControl_InputControlType.DPadLeft: return Math.Max(0f, -axes[DPadX]);
                    case InControl_InputControlType.DPadRight: return Math.Max(0f, axes[DPadX]);
                }
                if (!vita && (control == InControl_InputControlType.LeftTrigger || control == InControl_InputControlType.RightTrigger))
                {
                    if (separateTriggers) return Mathf.Clamp01(axes[control == InControl_InputControlType.LeftTrigger ? LeftTriggerAxis : RightTriggerAxis]);
                    float both = axes[Triggers];
                    return control == InControl_InputControlType.LeftTrigger ? Math.Max(0f, both) : Math.Max(0f, -both);
                }
                int button;
                if (!(vita ? VitaButtons : XInputButtons).TryGetValue((int)control, out button)) return 0f;
                return Input.GetKey((KeyCode)(firstButton + button)) ? 1f : 0f;
            }

            void Log()
            {
                var now = new int[30];
                for (int b = 0; b < 20; b++) now[b] = Input.GetKey((KeyCode)(firstButton + b)) ? 1 : 0;
                for (int a = 0; a < axes.Length; a++) now[20 + a] = (int)Math.Round(axes[a] * 10f);
                if (logged != null)
                    for (int i = 0; i < now.Length; i++)
                        if (now[i] != logged[i])
                            Debug.Log("T2D_INPUT joystick " + Joystick + (i < 20 ? " button " + i + (now[i] == 1 ? " down" : " up")
                                                                                  : " analog " + (i - 20) + " " + (now[i] / 10f).ToString("0.0")));
                logged = now;
            }
        }

        static readonly Reader[] readers = new Reader[4];

        // The pads there are now. The PS Vita's own pad is always there: Unity names it only some seconds after the start.
        public static List<Reader> Scan()
        {
            var found = new List<Reader>();
            string[] names = Input.GetJoystickNames();
            for (int i = 0; i < readers.Length; i++)
            {
                string name = i < names.Length ? names[i] : null;
                bool present = !string.IsNullOrEmpty(name) || (Vita && i == 0);
                if (!present) continue;
                if (readers[i] == null) readers[i] = new Reader(i + 1, string.IsNullOrEmpty(name) ? "PS Vita" : name, Vita);
                else if (!string.IsNullOrEmpty(name)) readers[i].Name = name;
                found.Add(readers[i]);
            }
            return found;
        }
    }
}
