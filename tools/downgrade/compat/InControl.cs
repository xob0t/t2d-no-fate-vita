using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using UnityEngine;

// The subset of InControl's API that the game's code uses, written anew over Unity's Input (Pad.cs), in place of the
// InControl assembly: the pipeline leaves InControl out (ROOTS "-InControl"), and the rewriter's naming convention
// sends every InControl type the game names to the type here of the same name with "InControl_" in front
// (InControl.PlayerActionSet -> T2DCompat.InControl_PlayerActionSet). Rewind's InControl has 649 methods with gaps;
// the game names 19 of its types and 89 of their members (docs/platform.md lists them).
//
// The members keep InControl's names and signatures, and the fields IL2CPP inlined into the game's code are public
// fields of the same names and types: the game reads OneAxisInputControl's enabled, ownerEnabled, thisState and
// lastState itself (InControl's WasPressed and State, inlined in UnityMain.inputsStandaloneInControl), and writes
// lowerDeadZone and stateThreshold. The enums keep InControl's values, which the game's code holds as constants.
//
// What it does, as InControl does for the game:
// - InputManager.UpdateInternal (from UnityMain.FixedUpdate) advances the tick, reads the pad (Pad.cs) into its
//   InputDevice's controls, then updates every PlayerActionSet alive: each action's value is the largest of its
//   bindings' (a device control's, or 1 for a key combination held), with its dead zone and state threshold.
// - A set without a Device reads InputManager.ActiveDevice (the game's join listener); one with a Device reads it.
// - Rebinding: ListenForBindingReplacing listens for the next control pressed (or key, by ListenOptions); the game's
//   OnBindingFound, OnBindingAdded and OnBindingRejected run as InControl runs them.
// - Save and Load write the regular bindings as text ("T2D1|Up=D11,K85;..."). A string from InControl's own Save
//   (a PC save's) doesn't load; Load then resets the set to its defaults.
// Left out: mouse bindings, touch, InControl's device profiles (Pad.cs has the two layouts the port needs),
// vibration and light colour (the PS Vita has neither; they do nothing), and InputManager's native and XInput
// options (set and ignored).
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    // ---- enums: InControl's values ----

    public enum InControl_InputControlType
    {
        None = 0,
        LeftStickUp = 1, LeftStickDown = 2, LeftStickLeft = 3, LeftStickRight = 4, LeftStickButton = 5,
        RightStickUp = 6, RightStickDown = 7, RightStickLeft = 8, RightStickRight = 9, RightStickButton = 10,
        DPadUp = 11, DPadDown = 12, DPadLeft = 13, DPadRight = 14,
        LeftTrigger = 15, RightTrigger = 16, LeftBumper = 17, RightBumper = 18,
        Action1 = 19, Action2 = 20, Action3 = 21, Action4 = 22,
        Back = 100, Start = 101, Select = 102, System = 103, Options = 104, Pause = 105, Menu = 106, Share = 107,
        Home = 108, View = 109, Power = 110, Capture = 111, Assistant = 112, Plus = 113, Minus = 114, Create = 115,
        Mute = 116, Guide = 117,
        LeftSL = 261, LeftSR = 262, RightSL = 263, RightSR = 264,
        LeftStickX = 301, LeftStickY = 302, RightStickX = 303, RightStickY = 304, DPadX = 305, DPadY = 306,
    }

    public enum InControl_InputDeviceStyle
    {
        Unknown, Xbox360, XboxOne, XboxSeriesX, PlayStation2, PlayStation3, PlayStation4, PlayStation5, PlayStationVita,
        PlayStationMove, Steam, SteamDeck, AppleMFi, AmazonFireTV, NVIDIAShield, NintendoNES, NintendoSNES, Nintendo64,
        NintendoGameCube, NintendoWii, NintendoWiiU, NintendoSwitch, GoogleStadia, Vive, Oculus, Logitech, Thrustmaster,
    }

    public enum InControl_BindingSourceType { None, DeviceBindingSource, KeyBindingSource, MouseBindingSource, UnknownDeviceBindingSource }

    public enum InControl_BindingSourceRejectionType { None, DuplicateBindingOnAction, DuplicateBindingOnActionSet }

    // InControl's Key, in its order (the game's keyboard bindings are these numbers). Keys.cs maps them to KeyCodes.
    public enum InControl_Key
    {
        None, Shift, Alt, Command, Control, LeftShift, LeftAlt, LeftCommand, LeftControl, RightShift, RightAlt,
        RightCommand, RightControl, Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Key0, Key1, Key2, Key3, Key4, Key5, Key6, Key7, Key8, Key9,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Backquote, Minus, Equals, Backspace, Tab, LeftBracket, RightBracket, Backslash, Semicolon, Quote, Return,
        Comma, Period, Slash, Space, Insert, Delete, Home, End, PageUp, PageDown, LeftArrow, RightArrow, UpArrow,
        DownArrow, Pad0, Pad1, Pad2, Pad3, Pad4, Pad5, Pad6, Pad7, Pad8, Pad9, Numlock, PadDivide, PadMultiply,
        PadMinus, PadPlus, PadEnter, PadPeriod, Clear, PadEquals, F13, F14, F15, AltGr, CapsLock, ExclamationMark,
        Tilde, At, Hash, Dollar, Percent, Caret, Ampersand, Asterisk, LeftParen, RightParen, Underscore, Plus,
        LeftBrace, RightBrace, Pipe, Colon, DoubleQuote, LessThan, GreaterThan, QuestionMark,
    }

    public delegate Vector2 InControl_DeadZoneFunc(float x, float y, float lowerDeadZone, float upperDeadZone);

    public struct InControl_InputControlState
    {
        public bool State;
        public float Value;
        public float RawValue;

        public void Set(float value, float threshold)
        {
            Value = value;
            State = value < -threshold || value > threshold;
        }
    }

    // ---- controls ----

    public class InControl_OneAxisInputControl
    {
        // Public: the game's code reads and writes these fields itself (inlined by IL2CPP).
        public bool enabled = true;
        public bool ownerEnabled = true;
        public InControl_InputControlState thisState;
        public InControl_InputControlState lastState;
        public float lowerDeadZone;
        public float upperDeadZone = 1f;
        public float stateThreshold;
        public bool Raw;

        InControl_InputControlState nextState;
        ulong pendingTick;
        bool clearInputState;

        public ulong UpdateTick { get; protected set; }
        public bool State { get { return enabled && ownerEnabled && thisState.State; } }
        public float Value { get { return enabled && ownerEnabled ? thisState.Value : 0f; } }
        public bool IsPressed { get { return State; } }
        public bool WasPressed { get { return enabled && ownerEnabled && thisState.State && !lastState.State; } }
        public bool WasReleased { get { return enabled && ownerEnabled && !thisState.State && lastState.State; } }
        public float LowerDeadZone { get { return lowerDeadZone; } set { lowerDeadZone = Mathf.Clamp01(value); } }
        public float UpperDeadZone { get { return upperDeadZone; } set { upperDeadZone = Mathf.Clamp01(value); } }
        public float StateThreshold { get { return stateThreshold; } set { stateThreshold = Mathf.Clamp01(value); } }

        void Prepare(ulong tick)
        {
            if (tick <= pendingTick) return;
            lastState = thisState;
            nextState = default(InControl_InputControlState);
            pendingTick = tick;
        }

        // The largest value of a tick wins, as InControl's UpdateWithValue: an action's bindings each give one.
        public void UpdateWithValue(float value, ulong tick)
        {
            Prepare(tick);
            if (Math.Abs(value) < Math.Abs(nextState.RawValue)) return;
            nextState.RawValue = value;
            nextState.Set(Raw ? value : ApplyDeadZone(value), stateThreshold);
        }

        float ApplyDeadZone(float value)
        {
            float magnitude = Math.Abs(value);
            if (magnitude < lowerDeadZone) return 0f;
            if (magnitude > upperDeadZone) return Math.Sign(value);
            float range = upperDeadZone - lowerDeadZone;
            return range <= 0f ? Math.Sign(value) : Math.Sign(value) * (magnitude - lowerDeadZone) / range;
        }

        public void Commit()
        {
            bool changed = thisState.State != nextState.State || Math.Abs(thisState.Value - nextState.Value) > 1e-7f;
            thisState = nextState;
            if (clearInputState)
            {
                lastState = nextState;
                clearInputState = false;
            }
            if (changed) UpdateTick = pendingTick;
        }

        public void CommitWithValue(float value, ulong tick)
        {
            UpdateWithValue(value, tick);
            Commit();
        }

        public void ClearInputState()
        {
            lastState = default(InControl_InputControlState);
            thisState = default(InControl_InputControlState);
            nextState = default(InControl_InputControlState);
            clearInputState = true;
        }

        public static implicit operator float(InControl_OneAxisInputControl control)
        {
            return control.Value;
        }
    }

    public class InControl_InputControl : InControl_OneAxisInputControl
    {
        public readonly InControl_InputControlType Target;
        public readonly string Handle;

        public InControl_InputControl(string handle, InControl_InputControlType target)
        {
            Handle = handle;
            Target = target;
        }
    }

    public class InControl_TwoAxisInputControl
    {
        // The game sets its own (UnityMain.BetterAnalog) for the up/down bias of its controller settings.
        public InControl_DeadZoneFunc DeadZoneFunc;
        float lowerDeadZone;
        float upperDeadZone = 1f;

        public float X { get; private set; }
        public float Y { get; private set; }
        public float LowerDeadZone { get { return lowerDeadZone; } set { lowerDeadZone = Mathf.Clamp01(value); } }
        public float UpperDeadZone { get { return upperDeadZone; } set { upperDeadZone = Mathf.Clamp01(value); } }
        public Vector2 Vector { get { return new Vector2(X, Y); } }

        // Applies the dead zone to a stick's raw axes (y up), as InControl's TwoAxisInputControl: the game's function if
        // it set one, else a circular dead zone.
        internal Vector2 Process(float x, float y, float lower)
        {
            Vector2 value = DeadZoneFunc != null ? DeadZoneFunc(x, y, lower, upperDeadZone) : Circular(x, y, lower, upperDeadZone);
            X = value.x;
            Y = value.y;
            return value;
        }

        public static Vector2 Circular(float x, float y, float lower, float upper)
        {
            var raw = new Vector2(x, y);
            float magnitude = raw.magnitude;
            if (magnitude < lower || magnitude < 1e-5f) return Vector2.zero;
            float range = upper - lower;
            float scaled = range <= 0f ? 1f : Mathf.Clamp01((magnitude - lower) / range);
            return raw / magnitude * scaled;
        }
    }

    // ---- devices ----

    public class InControl_InputDevice
    {
        public static readonly InControl_InputDevice Null = new InControl_InputDevice("None", InControl_InputDeviceStyle.Unknown, null);

        readonly Dictionary<int, InControl_InputControl> controls = new Dictionary<int, InControl_InputControl>();
        readonly InControl_TwoAxisInputControl leftStick = new InControl_TwoAxisInputControl();
        readonly InControl_TwoAxisInputControl rightStick = new InControl_TwoAxisInputControl();
        internal readonly Pad.Reader Reader;

        public string Name { get; private set; }
        public InControl_InputDeviceStyle DeviceStyle { get; private set; }
        public bool IsAttached { get; internal set; }
        public ulong LastInputTick { get; private set; }
        public bool IsNull { get { return Reader == null; } }

        public InControl_InputDevice(string name, InControl_InputDeviceStyle style, Pad.Reader reader)
        {
            Name = name;
            DeviceStyle = style;
            Reader = reader;
            float deadZone = reader == null ? 0.2f : reader.StickDeadZone;
            foreach (InControl_InputControlType type in Pad.Controls)
            {
                // As InControl: a stick's directions take the stick's value after its two-axis dead zone (Raw), and
                // their own lower dead zones only feed that dead zone (the game sets both, MainMenu.setControllerDeadZoneOnDevice).
                // A control counts as pressed (for the active device and rebinding) past half way.
                var control = new InControl_InputControl(Pad.ControlName(type), type) { stateThreshold = 0.5f };
                if (Pad.IsStickDirection(type))
                {
                    control.Raw = true;
                    control.lowerDeadZone = deadZone;
                }
                controls[(int)type] = control;
            }
            leftStick.LowerDeadZone = rightStick.LowerDeadZone = deadZone;
        }

        float LowerDeadZone(InControl_TwoAxisInputControl stick, InControl_InputControlType up)
        {
            float lower = stick.LowerDeadZone;
            for (InControl_InputControlType type = up; type <= up + 3; type++)
                lower = Math.Max(lower, GetControl(type).lowerDeadZone);
            return lower;
        }

        public InControl_InputControl GetControl(InControl_InputControlType type)
        {
            InControl_InputControl control;
            return controls.TryGetValue((int)type, out control) ? control : null;
        }

        public InControl_TwoAxisInputControl LeftStick { get { return leftStick; } }
        public InControl_TwoAxisInputControl RightStick { get { return rightStick; } }
        public InControl_InputControl LeftStickUp { get { return GetControl(InControl_InputControlType.LeftStickUp); } }
        public InControl_InputControl LeftStickDown { get { return GetControl(InControl_InputControlType.LeftStickDown); } }
        public InControl_InputControl LeftStickLeft { get { return GetControl(InControl_InputControlType.LeftStickLeft); } }
        public InControl_InputControl LeftStickRight { get { return GetControl(InControl_InputControlType.LeftStickRight); } }
        public InControl_InputControl LeftStickX { get { return GetControl(InControl_InputControlType.LeftStickX); } }
        public InControl_InputControl LeftStickY { get { return GetControl(InControl_InputControlType.LeftStickY); } }
        public InControl_InputControl RightStickUp { get { return GetControl(InControl_InputControlType.RightStickUp); } }
        public InControl_InputControl RightStickDown { get { return GetControl(InControl_InputControlType.RightStickDown); } }
        public InControl_InputControl RightStickLeft { get { return GetControl(InControl_InputControlType.RightStickLeft); } }
        public InControl_InputControl RightStickRight { get { return GetControl(InControl_InputControlType.RightStickRight); } }
        public InControl_InputControl RightStickX { get { return GetControl(InControl_InputControlType.RightStickX); } }
        public InControl_InputControl RightStickY { get { return GetControl(InControl_InputControlType.RightStickY); } }

        // Neither the PS Vita nor the test player's pad reader can do these.
        public void Vibrate(float leftMotor, float rightMotor) { }
        public void SetLightColor(float red, float green, float blue) { }

        internal void Update(ulong tick)
        {
            if (Reader == null) return;
            Reader.Poll();
            bool input = false;
            Vector2 left = leftStick.Process(Reader.Axis(Pad.LeftX), Reader.Axis(Pad.LeftY),
                LowerDeadZone(leftStick, InControl_InputControlType.LeftStickUp));
            Vector2 right = rightStick.Process(Reader.Axis(Pad.RightX), Reader.Axis(Pad.RightY),
                LowerDeadZone(rightStick, InControl_InputControlType.RightStickUp));
            foreach (InControl_InputControl control in controls.Values)
            {
                float value;
                switch (control.Target)
                {
                    case InControl_InputControlType.LeftStickUp: value = Math.Max(0f, left.y); break;
                    case InControl_InputControlType.LeftStickDown: value = Math.Max(0f, -left.y); break;
                    case InControl_InputControlType.LeftStickLeft: value = Math.Max(0f, -left.x); break;
                    case InControl_InputControlType.LeftStickRight: value = Math.Max(0f, left.x); break;
                    case InControl_InputControlType.LeftStickX: value = left.x; break;
                    case InControl_InputControlType.LeftStickY: value = left.y; break;
                    case InControl_InputControlType.RightStickUp: value = Math.Max(0f, right.y); break;
                    case InControl_InputControlType.RightStickDown: value = Math.Max(0f, -right.y); break;
                    case InControl_InputControlType.RightStickLeft: value = Math.Max(0f, -right.x); break;
                    case InControl_InputControlType.RightStickRight: value = Math.Max(0f, right.x); break;
                    case InControl_InputControlType.RightStickX: value = right.x; break;
                    case InControl_InputControlType.RightStickY: value = right.y; break;
                    default: value = Reader.Control(control.Target); break;
                }
                control.CommitWithValue(value, tick);
                if (control.State) input = true;
            }
            if (input) LastInputTick = tick;
        }

        // The control pressed this tick, for rebinding (InControl's DeviceBindingSourceListener): a button, a trigger
        // or a stick direction past half way.
        internal InControl_InputControl PressedNow()
        {
            foreach (InControl_InputControl control in controls.Values)
                if (control.WasPressed && Math.Abs(control.Value) >= 0.5f && !Pad.IsAxisPair(control.Target)) return control;
            return null;
        }
    }

    // ---- bindings ----

    public abstract class InControl_BindingSource : IEquatable<InControl_BindingSource>
    {
        public InControl_PlayerAction BoundTo { get; internal set; }
        public abstract string Name { get; }
        public abstract string DeviceName { get; }
        public abstract InControl_BindingSourceType BindingSourceType { get; }
        public abstract float GetValue(InControl_InputDevice device);
        public abstract bool Equals(InControl_BindingSource other);
        internal abstract string Save();

        public override bool Equals(object other)
        {
            return Equals(other as InControl_BindingSource);
        }

        public override int GetHashCode()
        {
            return Save().GetHashCode();
        }

        internal static InControl_BindingSource Load(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text[0] == 'D')
            {
                int control;
                if (!int.TryParse(text.Substring(1), out control)) return null;
                return new InControl_DeviceBindingSource((InControl_InputControlType)control);
            }
            if (text[0] == 'K')
            {
                var keys = new List<InControl_Key>();
                foreach (string part in text.Substring(1).Split('+'))
                {
                    int key;
                    if (!int.TryParse(part, out key)) return null;
                    keys.Add((InControl_Key)key);
                }
                return new InControl_KeyBindingSource(keys.ToArray());
            }
            return null;
        }
    }

    public class InControl_DeviceBindingSource : InControl_BindingSource
    {
        public InControl_InputControlType Control { get; private set; }

        public InControl_DeviceBindingSource(InControl_InputControlType control)
        {
            Control = control;
        }

        // As InControl: "" until the binding is on an action.
        public override string Name { get { return BoundTo == null ? "" : Pad.ControlName(Control); } }
        public override string DeviceName { get { return "Controller"; } }
        public override InControl_BindingSourceType BindingSourceType { get { return InControl_BindingSourceType.DeviceBindingSource; } }

        public override float GetValue(InControl_InputDevice device)
        {
            if (device == null) return 0f;
            InControl_InputControl control = device.GetControl(Control);
            return control == null ? 0f : control.Value;
        }

        public override bool Equals(InControl_BindingSource other)
        {
            var device = other as InControl_DeviceBindingSource;
            return device != null && device.Control == Control;
        }

        internal override string Save() { return "D" + (int)Control; }
    }

    // InControl's KeyCombo: up to eight keys held together.
    public struct InControl_KeyCombo
    {
        readonly InControl_Key[] keys;

        public InControl_KeyCombo(params InControl_Key[] keys)
        {
            this.keys = keys ?? new InControl_Key[0];
        }

        public int KeyCount { get { return keys == null ? 0 : keys.Length; } }
        public InControl_Key[] Keys { get { return keys ?? new InControl_Key[0]; } }

        public bool IsPressed
        {
            get
            {
                if (KeyCount == 0) return false;
                foreach (InControl_Key key in keys)
                    if (!T2DCompat.Keys.IsHeld(key)) return false;
                return true;
            }
        }

        public bool SameAs(InControl_KeyCombo other)
        {
            if (KeyCount != other.KeyCount) return false;
            for (int i = 0; i < KeyCount; i++)
                if (Array.IndexOf(other.Keys, keys[i]) < 0) return false;
            return true;
        }

        public override string ToString()
        {
            var names = new List<string>();
            foreach (InControl_Key key in Keys) names.Add(T2DCompat.Keys.Name(key));
            return string.Join(" + ", names.ToArray());
        }
    }

    public class InControl_KeyBindingSource : InControl_BindingSource
    {
        public InControl_KeyCombo Control { get; private set; }

        public InControl_KeyBindingSource(params InControl_Key[] keys)
        {
            Control = new InControl_KeyCombo(keys);
        }

        public override string Name { get { return Control.ToString(); } }
        public override string DeviceName { get { return "Keyboard"; } }
        public override InControl_BindingSourceType BindingSourceType { get { return InControl_BindingSourceType.KeyBindingSource; } }

        public override float GetValue(InControl_InputDevice device)
        {
            return Control.IsPressed ? 1f : 0f;
        }

        public override bool Equals(InControl_BindingSource other)
        {
            var key = other as InControl_KeyBindingSource;
            return key != null && key.Control.SameAs(Control);
        }

        internal override string Save()
        {
            var parts = new List<string>();
            foreach (InControl_Key key in Control.Keys) parts.Add(((int)key).ToString());
            return "K" + string.Join("+", parts.ToArray());
        }
    }

    public class InControl_BindingListenOptions
    {
        public bool IncludeControllers = true;
        public bool IncludeUnknownControllers;
        public bool IncludeNonStandardControls = true;
        public bool IncludeMouseButtons;
        public bool IncludeMouseScrollWheel;
        public bool IncludeKeys = true;
        public bool IncludeModifiersAsFirstClassKeys;
        public uint MaxAllowedBindings;
        public uint MaxAllowedBindingsPerType;
        public bool AllowDuplicateBindingsPerSet;
        public bool UnsetDuplicateBindingsOnSet;
        public bool RejectRedundantBindings;
        public InControl_BindingSource ReplaceBinding;
        public Func<InControl_PlayerAction, InControl_BindingSource, bool> OnBindingFound;
        public Action<InControl_PlayerAction, InControl_BindingSource> OnBindingAdded;
        public Action<InControl_PlayerAction, InControl_BindingSource, InControl_BindingSourceRejectionType> OnBindingRejected;
        public Action<InControl_PlayerAction> OnBindingEnded;
    }

    // ---- actions ----

    public class InControl_PlayerAction : InControl_OneAxisInputControl
    {
        readonly List<InControl_BindingSource> defaultBindings = new List<InControl_BindingSource>();
        readonly List<InControl_BindingSource> regularBindings = new List<InControl_BindingSource>();
        readonly ReadOnlyCollection<InControl_BindingSource> bindings;
        public InControl_BindingListenOptions ListenOptions;

        public string Name { get; private set; }
        public InControl_PlayerActionSet Owner { get; private set; }
        public ReadOnlyCollection<InControl_BindingSource> Bindings { get { return bindings; } }
        public bool IsListeningForBinding { get { return Owner.listenWithAction == this; } }

        public InControl_PlayerAction(string name, InControl_PlayerActionSet owner)
        {
            Name = name;
            Owner = owner;
            bindings = new ReadOnlyCollection<InControl_BindingSource>(regularBindings);
            owner.AddPlayerAction(this);
        }

        InControl_BindingListenOptions Options { get { return ListenOptions ?? Owner.ListenOptions; } }

        // A device control the platform's pad layout never reports (the PS Vita's bumpers, an XInput pad's Options) is
        // left out, so the controls menu shows only buttons there are (Pad.Offers).
        public void AddDefaultBinding(InControl_BindingSource binding)
        {
            if (binding == null || defaultBindings.Exists(b => b.Equals(binding))) return;
            var device = binding as InControl_DeviceBindingSource;
            if (device != null && !Pad.Offers(device.Control)) return;
            defaultBindings.Add(binding);
            AddBinding(binding);
        }

        public void AddDefaultBinding(params InControl_Key[] keys)
        {
            AddDefaultBinding(new InControl_KeyBindingSource(keys));
        }

        public void AddDefaultBinding(InControl_InputControlType control)
        {
            AddDefaultBinding(new InControl_DeviceBindingSource(control));
        }

        public bool AddBinding(InControl_BindingSource binding)
        {
            if (binding == null || HasBinding(binding)) return false;
            regularBindings.Add(binding);
            binding.BoundTo = this;
            return true;
        }

        public bool HasBinding(InControl_BindingSource binding)
        {
            return binding != null && regularBindings.Exists(b => b.Equals(binding));
        }

        public void RemoveBinding(InControl_BindingSource binding)
        {
            regularBindings.RemoveAll(b => b.Equals(binding));
        }

        public void ClearBindings()
        {
            regularBindings.Clear();
        }

        public void ResetBindings()
        {
            regularBindings.Clear();
            foreach (InControl_BindingSource binding in defaultBindings) AddBinding(binding);
        }

        public void ListenForBinding()
        {
            ListenForBindingReplacing(null);
        }

        public void ListenForBindingReplacing(InControl_BindingSource binding)
        {
            Options.ReplaceBinding = binding;
            Owner.listenWithAction = this;
            Owner.listenStartTick = InControl_InputManager.CurrentTick;
        }

        public void StopListeningForBinding()
        {
            if (Owner.listenWithAction != this) return;
            Owner.listenWithAction = null;
            InControl_BindingListenOptions options = Options;
            if (options.OnBindingEnded != null) options.OnBindingEnded(this);
        }

        internal void Update(ulong tick, InControl_InputDevice device)
        {
            // While the set listens for a binding, its actions read nothing (InControl's PreventInputWhileListeningForBinding).
            float value = 0f;
            if (Owner.listenWithAction == null)
                foreach (InControl_BindingSource binding in regularBindings)
                {
                    float v = binding.GetValue(device);
                    if (Math.Abs(v) > Math.Abs(value)) value = v;
                }
            CommitWithValue(value, tick);
        }

        // A new binding while listening (InControl's PlayerAction.DetectBindings).
        internal void Detect(InControl_InputDevice device)
        {
            if (Owner.listenWithAction != this || InControl_InputManager.CurrentTick <= Owner.listenStartTick) return;
            InControl_BindingListenOptions options = Options;
            InControl_BindingSource found = null;
            if (options.IncludeControllers && device != null && !device.IsNull)
            {
                InControl_InputControl control = device.PressedNow();
                if (control != null) found = new InControl_DeviceBindingSource(control.Target);
            }
            if (found == null && options.IncludeKeys)
            {
                InControl_Key key = T2DCompat.Keys.PressedNow(InControl_InputManager.CurrentTick, options.IncludeModifiersAsFirstClassKeys);
                if (key != InControl_Key.None) found = new InControl_KeyBindingSource(key);
            }
            if (found == null) return;
            if (options.OnBindingFound != null && !options.OnBindingFound(this, found)) return;
            if (HasBinding(found))
            {
                if (options.RejectRedundantBindings)
                {
                    if (options.OnBindingRejected != null) options.OnBindingRejected(this, found, InControl_BindingSourceRejectionType.DuplicateBindingOnAction);
                    return;
                }
                StopListeningForBinding();
                if (options.OnBindingAdded != null) options.OnBindingAdded(this, found);
                return;
            }
            if (options.UnsetDuplicateBindingsOnSet) Owner.RemoveBinding(found);
            if (!options.AllowDuplicateBindingsPerSet && Owner.HasBinding(found))
            {
                if (options.OnBindingRejected != null) options.OnBindingRejected(this, found, InControl_BindingSourceRejectionType.DuplicateBindingOnActionSet);
                return;
            }
            StopListeningForBinding();
            InControl_BindingSource replace = options.ReplaceBinding;
            int at = replace == null ? -1 : regularBindings.FindIndex(b => b.Equals(replace));
            if (at >= 0)
            {
                regularBindings[at] = found;
                found.BoundTo = this;
            }
            else
            {
                if (options.MaxAllowedBindingsPerType > 0)
                {
                    while (regularBindings.FindAll(b => b.BindingSourceType == found.BindingSourceType).Count >= options.MaxAllowedBindingsPerType)
                        regularBindings.RemoveAt(regularBindings.FindIndex(b => b.BindingSourceType == found.BindingSourceType));
                }
                else if (options.MaxAllowedBindings > 0)
                {
                    while (regularBindings.Count >= options.MaxAllowedBindings) regularBindings.RemoveAt(0);
                }
                AddBinding(found);
            }
            if (options.OnBindingAdded != null) options.OnBindingAdded(this, found);
        }

        internal string SaveBindings()
        {
            var parts = new List<string>();
            foreach (InControl_BindingSource binding in regularBindings) parts.Add(binding.Save());
            return Name + "=" + string.Join(",", parts.ToArray());
        }

        internal void LoadBindings(string text)
        {
            regularBindings.Clear();
            foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                InControl_BindingSource binding = InControl_BindingSource.Load(part);
                if (binding != null) AddBinding(binding);
            }
        }
    }

    public abstract class InControl_PlayerActionSet
    {
        const string SaveFormat = "T2D1";
        readonly List<InControl_PlayerAction> actions = new List<InControl_PlayerAction>();
        internal InControl_PlayerAction listenWithAction;
        internal ulong listenStartTick;

        public InControl_InputDevice Device { get; set; }
        public InControl_BindingListenOptions ListenOptions { get; set; }
        public bool Enabled { get; set; }
        public ulong UpdateTick { get; private set; }
        public bool IsListeningForBinding { get { return listenWithAction != null; } }
        public ReadOnlyCollection<InControl_PlayerAction> Actions { get { return actions.AsReadOnly(); } }

        protected InControl_PlayerActionSet()
        {
            Enabled = true;
            ListenOptions = new InControl_BindingListenOptions();
            InControl_InputManager.Attach(this);
        }

        protected InControl_PlayerAction CreatePlayerAction(string name)
        {
            return new InControl_PlayerAction(name, this);
        }

        internal void AddPlayerAction(InControl_PlayerAction action)
        {
            if (actions.Exists(a => a.Name == action.Name))
                throw new InvalidOperationException("The action set already has an action named " + action.Name);
            actions.Add(action);
        }

        public InControl_PlayerAction this[string name]
        {
            get { return actions.Find(a => a.Name == name); }
        }

        public void Destroy()
        {
            InControl_InputManager.Detach(this);
        }

        public void Reset()
        {
            listenWithAction = null;
            foreach (InControl_PlayerAction action in actions) action.ResetBindings();
        }

        public void ClearInputState()
        {
            foreach (InControl_PlayerAction action in actions) action.ClearInputState();
        }

        public bool HasBinding(InControl_BindingSource binding)
        {
            return actions.Exists(a => a.HasBinding(binding));
        }

        public void RemoveBinding(InControl_BindingSource binding)
        {
            foreach (InControl_PlayerAction action in actions) action.RemoveBinding(binding);
        }

        public string Save()
        {
            var text = new StringBuilder(SaveFormat);
            foreach (InControl_PlayerAction action in actions) text.Append('|').Append(action.SaveBindings());
            return text.ToString();
        }

        // Loads what Save wrote. Anything else (InControl's own format, from a PC save) leaves the defaults.
        public void Load(string data)
        {
            if (data == null || !data.StartsWith(SaveFormat + "|"))
            {
                Reset();
                return;
            }
            Reset();
            foreach (string entry in data.Substring(SaveFormat.Length + 1).Split('|'))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                InControl_PlayerAction action = this[entry.Substring(0, equals)];
                if (action != null) action.LoadBindings(entry.Substring(equals + 1));
            }
        }

        internal void Update(ulong tick)
        {
            InControl_InputDevice device = Device ?? InControl_InputManager.ActiveDevice;
            foreach (InControl_PlayerAction action in actions.ToArray())
            {
                action.ownerEnabled = Enabled;
                action.Update(tick, device);
                if (action.UpdateTick > UpdateTick) UpdateTick = action.UpdateTick;
            }
            InControl_PlayerAction listening = listenWithAction;
            if (listening != null) listening.Detect(device);
        }
    }

    // ---- the manager ----

    public static class InControl_InputManager
    {
        // Fields, not events: the game adds its handlers with Delegate.Combine and an interlocked exchange on them.
        public static Action<InControl_InputDevice> OnDeviceAttached;
        public static Action<InControl_InputDevice> OnDeviceDetached;
        // With Rewind's patch 0027 the inlined accessors are folded back, and the game calls these instead.
        public static void add_OnDeviceAttached(Action<InControl_InputDevice> value) { OnDeviceAttached += value; }
        public static void remove_OnDeviceAttached(Action<InControl_InputDevice> value) { OnDeviceAttached -= value; }
        public static void add_OnDeviceDetached(Action<InControl_InputDevice> value) { OnDeviceDetached += value; }
        public static void remove_OnDeviceDetached(Action<InControl_InputDevice> value) { OnDeviceDetached -= value; }
        public static Action<InControl_InputDevice> OnActiveDeviceChanged;

        static readonly List<InControl_PlayerActionSet> sets = new List<InControl_PlayerActionSet>();
        static readonly List<InControl_InputDevice> devices = new List<InControl_InputDevice>();
        static InControl_InputDevice active = InControl_InputDevice.Null;
        static ulong tick;
        static bool setUp;
        static float nextScan;

        internal static ulong CurrentTick { get { return tick; } }

        // InControl's options, which the game sets in UnityMain.OnEnable; Pad.cs reads Unity's Input either way.
        public static bool InvertYAxis { get; set; }
        public static bool SuspendInBackground { get; set; }
        public static bool EnableICade { get; set; }
        public static bool EnableXInput { get; set; }
        public static uint XInputUpdateRate { get; set; }
        public static uint XInputBufferSize { get; set; }
        public static bool EnableNativeInput { get; set; }
        public static bool NativeInputEnableXInput { get; set; }
        public static bool NativeInputEnableMFi { get; set; }
        public static uint NativeInputUpdateRate { get; set; }
        public static bool NativeInputPreventSleep { get; set; }

        public static InControl_InputDevice ActiveDevice { get { return active; } }
        public static ReadOnlyCollection<InControl_InputDevice> Devices { get { return devices.AsReadOnly(); } }

        public static bool SetupInternal()
        {
            if (setUp) return true;
            setUp = true;
            tick = 0;
            ScanDevices();
            return true;
        }

        public static void ResetInternal()
        {
            foreach (InControl_InputDevice device in devices.ToArray()) Detach(device);
            active = InControl_InputDevice.Null;
            setUp = false;
        }

        public static void UpdateInternal()
        {
            if (!setUp) SetupInternal();
            if (Time.realtimeSinceStartup >= nextScan) ScanDevices();
            tick++;
            InControl_InputDevice latest = null;
            foreach (InControl_InputDevice device in devices)
            {
                device.Update(tick);
                if (device.LastInputTick == tick) latest = device;
            }
            if (latest == null && active.IsNull && devices.Count > 0) latest = devices[0];
            if (latest != null && latest != active)
            {
                active = latest;
                if (OnActiveDeviceChanged != null) OnActiveDeviceChanged(active);
            }
            foreach (InControl_PlayerActionSet set in sets.ToArray()) set.Update(tick);
        }

        public static void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            foreach (InControl_PlayerActionSet set in sets) set.ClearInputState();
        }

        public static void OnLevelWasLoaded()
        {
            foreach (InControl_PlayerActionSet set in sets) set.ClearInputState();
        }

        internal static void Attach(InControl_PlayerActionSet set)
        {
            if (!sets.Contains(set)) sets.Add(set);
        }

        internal static void Detach(InControl_PlayerActionSet set)
        {
            sets.Remove(set);
        }

        // Once a second: a pad that comes or goes (a test player's; the PS Vita's is always there).
        static void ScanDevices()
        {
            nextScan = Time.realtimeSinceStartup + 1f;
            List<Pad.Reader> found = Pad.Scan();
            foreach (InControl_InputDevice device in devices.ToArray())
                if (!found.Contains(device.Reader)) Detach(device);
            foreach (Pad.Reader reader in found)
            {
                if (devices.Exists(d => d.Reader == reader)) continue;
                var device = new InControl_InputDevice(reader.Name, reader.Style, reader) { IsAttached = true };
                devices.Add(device);
                Debug.Log("T2D_INPUT attached " + reader.Name + " (" + reader.Style + ")");
                if (OnDeviceAttached != null) OnDeviceAttached(device);
            }
        }

        static void Detach(InControl_InputDevice device)
        {
            devices.Remove(device);
            device.IsAttached = false;
            Debug.Log("T2D_INPUT detached " + device.Name);
            if (active == device) active = InControl_InputDevice.Null;
            if (OnDeviceDetached != null) OnDeviceDetached(device);
        }
    }
}
