// Minimal reproductions of Rewind issues (docs/rewind-issues.md), written from scratch: nothing here comes from a
// game. Each method reproduces a native code pattern that an IL2CPP Master build (MSVC /GL /LTCG) makes of
// ordinary C#, and that Rewind recovers wrongly or leaves unmarked. Method names start with the issue number.
// Callees that must stay calls are [MethodImpl(NoInlining)], which IL2CPP turns into __declspec(noinline).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Repros
{
    // The BitConverter members a stripped game build typically keeps (DoubleToInt64Bits and SingleToInt32Bits,
    // which Rewind uses to show bit patterns) without the ones that convert back (Int64BitsToDouble), so that
    // UnityLinker strips them as it did in the game where issue 2 was found.
    public static class KeepBitConverter
    {
        public static long DoubleBits(double d) => BitConverter.DoubleToInt64Bits(d);
        public static int SingleBits(float f) => BitConverter.SingleToInt32Bits(f);
    }

    public static class Sink
    {
        public static double D;
        public static int I;
        public static object O;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TakeDouble(double v) { D = v; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TakeDoubleBool(double v, bool flag) { D = v; I = flag ? 1 : 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TakeInt(int v) { I = v; }
    }

    // ---- 13: floating-point negation (xorps with a sign mask) passed as a double argument ----

    public sealed class Particle
    {
        public int Image, Frame, Layer;
        public bool Animated;
        public double Vx, Vy, Gravity;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Particle(int image, int frame, bool animated, double vx, double vy, double gravity, int layer)
        {
            Image = image; Frame = frame; Animated = animated; Vx = vx; Vy = vy; Gravity = gravity; Layer = layer;
        }
    }

    public static class Issue13
    {
        public static int Mode;
        public static System.Random Rng = new System.Random(1);
        public static Particle[] Spawned = new Particle[8];

        // The negated value is a stack argument of a constructor call, with the sign mask hoisted out of the loop.
        public static void Issue13_NegatedStackArgument(int count)
        {
            for (int i = 0; i < count; i++)
            {
                double vx = Mode == 8 ? 0.0 : Rng.NextDouble() * 16.0 - 8.0;
                double vy = Mode == 8 ? 0.0 : -(Rng.NextDouble() * 6.0 + 2.0);
                Spawned[i & 7] = new Particle(57, 0, true, vx, vy, 0.0, i);
            }
        }

        // The negated value is a register argument of a static call.
        public static void Issue13_NegatedRegisterArgument(double angle)
        {
            Sink.TakeDouble(-Math.Sin(angle));
        }

        // The same, with a negated field.
        public static void Issue13_NegatedFieldArgument(Particle p)
        {
            Sink.TakeDoubleBool(-p.Vy, p.Animated);
        }
    }

    // ---- 2: a double merged from two paths ("bits reinterpreted as double", no Int64BitsToDouble) ----

    public abstract class Fader
    {
        public abstract double GetAlpha();
        public abstract void SetAlpha(double alpha);

        // The clamp keeps 1.0 and the sum in registers and merges them before the virtual call.
        public void Issue2_ClampedAlpha()
        {
            double a = GetAlpha();
            if (a < 1.0)
            {
                a += 0.05;
                SetAlpha(a > 1.0 ? 1.0 : a);
            }
        }
    }

    public static class Issue2
    {
        public static double SavedX, SavedY;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SaveX(double x, bool save) { SavedX = x; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SaveY(double y, bool save) { SavedY = y; }

        // One branch stores a field's value, the other 0.0; the merged value is passed on after both.
        public static void Issue2_MergedArgument(Particle p, bool alive)
        {
            double y;
            if (alive && p != null)
            {
                SaveX(p.Vx, false);
                y = p.Vy;
            }
            else
            {
                SaveX(0.0, false);
                y = 0.0;
            }
            SaveY(y, false);
        }
    }

    // ---- 19 and 10: 8-byte structs returned in RAX ----

    public struct FloatPair
    {
        public float A, B;
        public FloatPair(float a, float b) { A = a; B = b; }
    }

    public static class Issue19
    {
        public static double Bias = 0.5;

        // Like an analog-stick filter: every return is a Vector2 built in RAX from two floats.
        public static Vector2 Issue19_SnapVector2(float x, float y, float deadZone)
        {
            float ax = Math.Abs(x) < deadZone ? 0f : Math.Abs(x);
            float ay = Math.Abs(y) < deadZone ? 0f : Math.Abs(y);
            if (ax < 1e-5f && ay < 1e-5f)
                return Vector2.zero;
            if (ax != 0f && ay != 0f)
            {
                float ratio = ax > ay ? ay / ax : ax / ay;
                if (ratio > Bias)
                    return new Vector2(Math.Sign(x), Math.Sign(y));
            }
            if (ay > ax)
                return new Vector2(0f, Math.Sign(y));
            return new Vector2(Math.Sign(x), 0f);
        }

        // The same with a struct of our own.
        public static FloatPair Issue19_OrderedPair(float a, float b)
        {
            if (a > b)
                return new FloatPair(b, a);
            return new FloatPair(a, b);
        }
    }

    // ---- 8, 7: a ulong at offset 0 of a struct ----

    public struct ULongId
    {
        public ulong Value;

        // 8: UInt64.GetHashCode inlined, then truncated to the int return.
        public override int GetHashCode() => Value.GetHashCode();

        // 7: the field's address is the struct's own address.
        public override string ToString() => Value.ToString();

        public override bool Equals(object obj) => obj is ULongId other && other.Value == Value;
    }

    public struct StatsReceived
    {
        public ulong GameId;
        public int Result;
        public ulong UserId;
    }

    public static class Issue7
    {
        public static string Last;

        // 7: the address of a by-value parameter's first field.
        public static void Issue7_ParameterFieldToString(StatsReceived callback)
        {
            if (callback.Result == 1)
                Last = callback.GameId.ToString();
        }
    }

    public sealed class KeyChord
    {
        public ulong Keys;
        public int Count;

        // 8: an int added to the inlined UInt64 hash.
        public override int GetHashCode() => Keys.GetHashCode() + Count;

        public override bool Equals(object obj) => obj is KeyChord k && k.Keys == Keys && k.Count == Count;
    }

    // ---- 16: two adjacent bool fields set by one 2-byte store ----

    public class Flags16
    {
        public int State;
        public bool Visible;
        public bool CanBeHit;
        public bool Grounded;

        public void Issue16_SetTwoBools(int state)
        {
            State = state;
            Visible = true;
            CanBeHit = false;
        }
    }

    // The same with the fields declared in a base class, as in a game object hierarchy.
    public class FlagsBase
    {
        public int Kind;
        public bool Important, Active, Visible, CanBeHit, CanHitPlayer, CanHitEnemies, CanBeMeleed, Bleeds;
        public double X;
    }

    public class FlagsDerived : FlagsBase
    {
        public int Extra;

        public void Issue16_SetInheritedBools(int kind)
        {
            Kind = kind;
            Visible = true;
            CanBeHit = false;
        }

        public void Issue16_ClearInheritedBools()
        {
            CanBeHit = false;
            CanHitPlayer = false;
            Extra = 3;
        }

        public void Issue16_SetFourInheritedBools()
        {
            Important = false;
            Active = true;
            Visible = true;
            CanBeHit = false;
        }
    }

    // ---- 17: a virtual call whose vtable load was hoisted above a branch ----

    public abstract class Actor
    {
        public int Row;

        public abstract bool IsBusy(int what);
        public abstract void ChangeAnim(int row, int frame, int speed);
        public abstract void ChangeState(int state);

        public void Issue17_MergedVirtualCall()
        {
            if (IsBusy(0))
            {
                ChangeState(IsBusy(1) ? 44 : 5);
                return;
            }
            ChangeAnim(41, 0, 4);
            Row = 3;
        }
    }

    public static class Issue17
    {
        public static System.Random Rng = new System.Random(2);
        public static int Result;

        public static void Issue17_MergedRandomNext(int state, Actor a)
        {
            int low;
            if (state == 2)
            {
                a.Row = 1;
                low = 90;
            }
            else
            {
                a.Row = 2;
                low = 120;
            }
            Result = Rng.Next(low, 180);
        }

        public static int Difficulty, Mode, Timer;

        // Two identical calls on paths that then join: the compiler keeps one call site (tail merging), and the
        // receiver and its class come from different blocks.
        public static void Issue17_TailMergedStaticReceiver(int state)
        {
            int t;
            switch (state)
            {
                case 1:
                    if (Mode != 8)
                        t = Rng.Next(60, 180);
                    else
                        t = 120;
                    break;
                case 2:
                    if (Mode != 8)
                        t = Rng.Next(120, 180);
                    else
                        t = 150;
                    break;
                default:
                    t = 30;
                    break;
            }
            Timer = t;
            if (Difficulty == 0)
                Timer += 60;
        }
    }

    // No static constructor, so reading the field needs no class-init check and the two call paths are alike.
    public static class RandomHolder
    {
        public static System.Random Rng;
        public static int Low, High;
    }

    public static class Issue17b
    {
        public static int Issue17_TailMergedNoCctor(int state, int mode)
        {
            int t;
            if (state == 1)
            {
                if (mode != 8)
                    t = RandomHolder.Rng.Next(60, 180);
                else
                    t = 120;
            }
            else
            {
                if (mode != 8)
                    t = RandomHolder.Rng.Next(120, 180);
                else
                    t = 150;
            }
            return t + 1;
        }

        public static int Issue17_TailMergedFieldArgs(int state)
        {
            int t;
            if (state == 1)
                t = RandomHolder.Rng.Next(RandomHolder.Low, 180);
            else if (state == 2)
                t = RandomHolder.Rng.Next(RandomHolder.High, 180);
            else
                t = 0;
            return t * 2;
        }
    }

    public abstract class Walker
    {
        public int Row, Input, Frame;
        public double Vx;

        public abstract bool Pressed(int key);
        public abstract void ChangeAnim(int row, int frame, int speed);
        public abstract void LimitSpeed(double max);

        // The same call on 'this' at the end of several paths.
        public void Issue17_TailMergedThisCall()
        {
            if (Pressed(3))
            {
                Vx -= 0.5;
                if (Row != 7)
                {
                    ChangeAnim(7, 0, 4);
                    return;
                }
            }
            else if (Pressed(2))
            {
                Vx += 0.5;
                if (Row != 8)
                {
                    ChangeAnim(8, 0, 4);
                    return;
                }
            }
            else if (Row != 1)
            {
                ChangeAnim(Input == 0 ? 1 : 2, 0, 4);
                return;
            }
            LimitSpeed(1.0);
        }

        // The class pointer for the last call is loaded on both paths (each after another virtual call) and
        // merged.
        public void Issue17_MergedClassPointer()
        {
            int row;
            if (Pressed(3))
            {
                Vx -= 0.5;
                row = 7;
            }
            else
            {
                if (Pressed(2))
                    Vx += 0.5;
                row = 8;
            }
            ChangeAnim(row, 0, 4);
        }

        public void Issue17_MergedClassPointerTail(int input)
        {
            if (input == 0)
            {
                if (Pressed(1))
                    return;
                ChangeAnim(5, 0, 4);
            }
            else
            {
                if (Pressed(input))
                    Frame = 0;
                ChangeAnim(44, 0, 4);
            }
        }
    }

    // ---- 18: an invoker specialized by LTCG for one vtable slot ----

    public class Shooter
    {
        public double X, Y, Vx, Vy;
        public int Image, Damage, Facing, Types, Index;
        public bool KnockDown, Update;
        public object[] Sounds;

        public virtual void SetupImpact(double x, double y, int width, int height, bool knockDown, int damage,
            int facing, double vx, double vy, int hitTypes, object[] hitSounds, bool update, bool increaseIndex)
        {
            X = x; Y = y; Damage = damage + width + height; KnockDown = knockDown; Facing = facing;
            Vx = vx; Vy = vy; Types = hitTypes; Sounds = hitSounds; Update = update;
            if (increaseIndex)
                Index++;
        }

        public void Issue18_InvokerCallA()
        {
            int damage = Image != 230 ? Damage : Damage * 2;
            SetupImpact(X + 4.0, Y, 32, 32, KnockDown, damage, Facing, Facing * 2.0, Vy, Types, Sounds, true, KnockDown);
        }

        public void Issue18_InvokerCallB(int damage)
        {
            SetupImpact(X, Y - 8.0, 24, 16, false, damage, -Facing, Vx, 0.0, 3, Sounds, false, true);
        }

        public void Issue18_InvokerCallC(Shooter other)
        {
            other.SetupImpact(X, Y, 8, 8, true, 1, Facing, 0.0, -2.0, Types, null, Update, false);
        }
    }

    // In a large method MSVC stops inlining, so the 13-argument invoker stays a function of its own; every call
    // to it passes the same slot, so LTCG specializes it for that slot and the slot argument (RCX) goes dead.
    public class Brawler
    {
        public double X, Y, Vy;
        public int Image, Damage, Facing, Types, State, Timer, Anim;
        public bool Grounded, Knock;
        public object[] Sounds;

        public virtual void SetupHitbox(double x, double y, int width, int height, bool knockDown, int damage,
            int facing, double vx, double vy, int hitTypes, object[] hitSounds, bool update, bool increaseIndex)
        {
            X = x + vx; Y = y + vy; Damage = damage + width * height + hitTypes; Knock = knockDown ^ update ^ increaseIndex;
            Facing = facing; Sounds = hitSounds;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PlaySound(int id) { Timer += id; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ChangeImage(int id) { Image = id; }

        public void Issue18_SpecializedInvoker(int state)
        {
            State = state;
            Timer = 0;
            switch (state)
            {
                case 0:
                    ChangeImage(10);
                    SetupHitbox(X + 8, Y, 32, 32, Grounded, Image != 230 ? Damage : Damage * 2, Facing, Facing * 2.0, Vy, Types, Sounds, true, Grounded);
                    break;
                case 1:
                    ChangeImage(11);
                    PlaySound(3);
                    SetupHitbox(X - 8, Y + 4, 24, 40, false, Damage + 1, -Facing, 0.0, -1.5, 3, Sounds, false, true);
                    break;
                case 2:
                    ChangeImage(12);
                    if (Grounded)
                        SetupHitbox(X, Y - 16, 16, 16, true, 5, Facing, Facing * 4.0, -4.0, Types | 2, null, true, false);
                    else
                        SetupHitbox(X, Y + 16, 16, 16, true, 7, Facing, Facing * 3.0, 2.0, Types | 4, null, true, false);
                    break;
                case 3:
                    ChangeImage(13);
                    PlaySound(4);
                    for (int i = 0; i < Anim; i++)
                        SetupHitbox(X + i * 8, Y, 8, 8, false, 1, Facing, 0.0, 0.0, 1, Sounds, i == 0, true);
                    break;
                case 4:
                    ChangeImage(14);
                    SetupHitbox(X, Y, 48, 48, Knock, Damage * 3, Facing, Facing * 6.0, -6.0, Types, Sounds, true, true);
                    PlaySound(5);
                    break;
                case 5:
                    ChangeImage(15);
                    SetupHitbox(X + Facing * 20, Y - 10, 20, 10, false, Damage / 2, Facing, Facing * 1.0, 0.5, Types, Sounds, Grounded, false);
                    break;
                case 6:
                    ChangeImage(16);
                    PlaySound(6);
                    SetupHitbox(X, Y, 64, 8, Grounded, Damage, Facing, 0.0, Vy, 7, null, false, Knock);
                    break;
                case 7:
                    ChangeImage(17);
                    SetupHitbox(X - Facing * 12, Y, 12, 30, true, Damage + Anim, -Facing, -Facing * 2.0, -2.0, Types, Sounds, true, false);
                    PlaySound(7);
                    break;
                case 8:
                    ChangeImage(18);
                    SetupHitbox(X, Y + 30, 30, 4, false, 1, Facing, 0.0, 8.0, 1, Sounds, false, false);
                    break;
                case 9:
                    ChangeImage(19);
                    PlaySound(9);
                    SetupHitbox(X + 2, Y + 2, 2, 2, Knock, Damage, Facing, Facing * 9.0, Vy * 2.0, Types, Sounds, Grounded, Knock);
                    break;
                default:
                    ChangeImage(0);
                    break;
            }
            Anim++;
        }
    }

    // ---- 6: typeof(T) passed on: "no field at offset 0x20 of object", noted but not marked ----

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct PackSentinel
    {
        public uint A;
        public ulong B;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PackedRecord
    {
        public int Result;
        public ulong Id;
        public int Count;
    }

    public class ProfileA { }
    public class ProfileB { }

    public static class Issue6
    {
        public static List<Type> Profiles = new List<Type>();

        public static bool Issue6_TypeofSizeOf()
        {
            int a = Marshal.SizeOf(typeof(PackSentinel));
            int b = Marshal.SizeOf(typeof(PackedRecord));
            return a == 16 && b == 16;
        }

        public static void Issue6_TypeofList()
        {
            Profiles.Add(typeof(ProfileA));
            Profiles.Add(typeof(ProfileB));
        }
    }

    // ---- 11: constructors whose base constructor call was inlined away ----

    public class EmptyBehaviour : MonoBehaviour
    {
        public int Count;
    }

    public class EmptyBase { }

    public class EmptyDerived : EmptyBase
    {
        public int Value;
    }

    // ---- inlined constructors (issue 20's last item) ----

    public sealed class Tint
    {
        public double RedMul, GreenMul, BlueMul, AlphaMul;
        public double RedOff, GreenOff, BlueOff, AlphaOff;
        public double RedOffN, GreenOffN, BlueOffN, AlphaOffN;
        public uint Packed;

        public Tint(double redMul = 1, double greenMul = 1, double blueMul = 1, double alphaMul = 1,
            double redOff = 0, double greenOff = 0, double blueOff = 0, double alphaOff = 0)
        {
            BlueMul = blueMul; GreenMul = greenMul; RedMul = redMul; AlphaMul = alphaMul;
            BlueOff = blueOff; GreenOff = greenOff; RedOff = redOff; AlphaOff = alphaOff;
            BlueOffN = blueOff / 255.0; GreenOffN = greenOff / 255.0; RedOffN = redOff / 255.0; AlphaOffN = alphaOff / 255.0;
            CalcMul();
            CalcOff();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CalcMul() { Packed = (uint)(RedMul * 255) << 16 | (uint)(GreenMul * 255) << 8 | (uint)(BlueMul * 255); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CalcOff() { Packed ^= (uint)(AlphaOff * 255); }
    }

    // The same, but not sealed and calling virtual methods, with struct fields set by them.
    public class VirtualTint
    {
        public double RedMul, GreenMul, BlueMul, AlphaMul;
        public double RedOff, GreenOff, BlueOff, AlphaOff;
        public double AlphaOffN, BlueOffN, GreenOffN, RedOffN;
        public Color Mul;
        public uint MulArgb;
        public Vector4 Off;

        public VirtualTint(double redMul = 1, double greenMul = 1, double blueMul = 1, double alphaMul = 1,
            double redOff = 0, double greenOff = 0, double blueOff = 0, double alphaOff = 0)
        {
            BlueMul = blueMul; GreenMul = greenMul; RedMul = redMul; AlphaMul = alphaMul;
            BlueOff = blueOff; GreenOff = greenOff; RedOff = redOff; AlphaOff = alphaOff;
            BlueOffN = blueOff / 255.0; GreenOffN = greenOff / 255.0; RedOffN = redOff / 255.0; AlphaOffN = alphaOff / 255.0;
            CalcMul();
            CalcOff();
        }

        public virtual void CalcMul()
        {
            Mul = new Color((float)RedMul, (float)GreenMul, (float)BlueMul, (float)AlphaMul);
            MulArgb = (uint)((int)(AlphaMul * 255) << 24 | (int)(RedMul * 255) << 16 | (int)(GreenMul * 255) << 8 | (int)(BlueMul * 255));
        }

        public virtual void CalcOff()
        {
            Off = new Vector4((float)RedOffN, (float)GreenOffN, (float)BlueOffN, (float)AlphaOffN);
        }
    }

    public static class InlinedCtor
    {
        public static Tint Logo;
        public static VirtualTint Fade;
        public static int Timer;

        public static void InlinedCtor_NewTint()
        {
            Logo = new Tint(1, 1, 1, 0);
            Timer = 25;
        }

        public static void InlinedCtor_NewVirtualTint()
        {
            Fade = new VirtualTint(1, 1, 1, 0);
            Timer = 25;
        }

        public static VirtualTint InlinedCtor_NewVirtualTintLocal(double alpha)
        {
            VirtualTint t = new VirtualTint(0, 0, 0, alpha);
            Timer = 10;
            return t;
        }
    }

    // ---- 9: RuntimeHelpers.GetHashCode inlined into arithmetic on the reference ----

    public abstract class BindingLike
    {
        public override int GetHashCode() => base.GetHashCode();
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
    }

    public static class Issue9
    {
        public static int Issue9_IdentityHash(object o) => RuntimeHelpers.GetHashCode(o);
    }

    // ---- 10: small structs handled as integers ----

    public struct OptionalU16
    {
        public bool HasValue;
        public ushort Value;
    }

    public struct PipeHandle
    {
        public int Value;
        public PipeHandle(int value) { Value = value; }
    }

    public sealed class Matcher
    {
        public OptionalU16 VendorField;

        public OptionalU16 Issue10_VendorID
        {
            get => VendorField;
            set => VendorField = value;
        }
    }

    public static class Issue10
    {
        public static int Counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int NextHandle() => ++Counter;

        public static PipeHandle Issue10_IntAsStruct() => new PipeHandle(NextHandle());
    }

    // ---- 3: the incremental GC's write barrier after a static field store ----

    public sealed class Registry
    {
        private static Registry s_instance;
        public object Item;

        public static Registry Issue3_GetInstance()
        {
            if (s_instance == null)
                s_instance = new Registry();
            return s_instance;
        }

        // For comparison: the instance-field barrier, which Rewind removes.
        public void Issue3_SetInstanceField(object item)
        {
            Item = item;
        }
    }

    // ---- 1 and 15: new T[n] calls Array::NewSpecific directly ----

    public static class Issue1
    {
        public static int[] Issue1_NewIntArray(int n) => new int[n];

        public static string[] Issue1_NewStringArray(int n)
        {
            string[] a = new string[n];
            if (n > 0)
                a[0] = "first";
            return a;
        }
    }

    // ---- 4: signed modulo by a power of two ----

    public static class Issue4
    {
        public static int Issue4_Mod4(int ticks) => ticks % 4;
    }

    // ---- 29: a switch through a byte-indexed jump table ----
    // Many case values sharing few bodies: MSVC writes a byte per value (the index of its body) and a table of the
    // bodies' addresses, "movzx eax, byte [base + value + bytes]; mov ecx, [base + rax*4 + table]; jmp".

    public static class Issue29
    {
        public static int Count;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk(int v) { Count += v; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Climb(int v) { Count -= v; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Swim(int v) { Count ^= v; }

        // 30 values, 3 bodies and the default.
        public static void Issue29_ByteTable(int tile)
        {
            switch (tile)
            {
                case 0: case 1: case 2: case 5: case 8: case 9: case 13: case 21: case 22: case 29:
                    Walk(tile);
                    break;
                case 3: case 4: case 10: case 11: case 17: case 18: case 25:
                    Climb(tile);
                    break;
                case 6: case 7: case 14: case 15: case 16: case 23: case 24: case 28:
                    Swim(tile);
                    break;
            }
        }

        // The same from 400: the byte table is indexed by id - 400.
        public static void Issue29_ByteTableFrom400(int id)
        {
            switch (id)
            {
                case 400: case 401: case 404: case 405: case 409: case 412: case 413: case 420: case 427:
                    Walk(id);
                    break;
                case 402: case 403: case 410: case 411: case 418: case 419: case 426:
                    Climb(id);
                    break;
                case 406: case 407: case 414: case 415: case 416: case 422: case 423: case 429:
                    Swim(id);
                    break;
            }
        }
    }

    // ---- 30: a value kept in a volatile register across the write barrier ----
    // MSVC (LTCG) knows the barrier helper writes only rax, rcx, rdx and r8, and keeps a value in r9 or an xmm
    // register across the call: "xor r9d, r9d; mov [obj+a], r9; call <barrier>; mov [obj+b], r9d". MSVC 14.50
    // calls the barrier in the two loops that return on a hit (the value in xmm2, the object in r9, this in r11) and
    // inlines it in the other three, which are controls.

    public sealed class Issue30
    {
        public object StopPoint;
        public int Name;
        public int Type;
        public bool Important;
        public object Platform;
        public bool Grounded;
        public double Y;
        public Issue30 Collider;
        public Issue30 Cover;
        public object A, B, C, D;

        // Several reference stores, then the zero stored as null and, after the barrier, as an int.
        public static void Issue30_ZeroAfterStores(Issue30 o, object a, object b, object c, object d)
        {
            o.A = a;
            o.B = b;
            o.C = c;
            o.D = d;
            o.StopPoint = null;
            o.Name = 231;
            o.Type = 0;
            o.Important = true;
        }

        // The same in a loop over objects.
        public static void Issue30_ZeroInLoop(Issue30[] all)
        {
            for (int i = 0; i < all.Length; i++)
            {
                Issue30 o = all[i];
                o.StopPoint = null;
                o.Type = 0;
                o.Important = true;
            }
        }

        // A double computed before the barrier and stored after it, after other reference stores.
        public static void Issue30_DoubleAfterStores(Issue30 o, object platform, object a, double y, double offset)
        {
            o.A = a;
            o.B = a;
            double top = y + offset;
            if (top <= o.Y)
            {
                o.Platform = platform;
                o.Grounded = true;
                o.Y = top - 1.0;
            }
        }

        // The same in a loop: the first object below the top lands on it.
        public static bool Issue30_DoubleInLoop(Issue30[] all, object platform, double y, double offset)
        {
            double top = y + offset;
            for (int i = 0; i < all.Length; i++)
            {
                Issue30 o = all[i];
                if (o != null && top <= o.Y)
                {
                    o.Platform = platform;
                    o.Grounded = true;
                    o.Y = top - 1.0;
                    return true;
                }
            }
            return false;
        }

        // A reference read from an array in a loop, stored into a field (barrier) and then into another (barrier).
        public bool Issue30_ReferenceInLoop(Issue30[] sprites)
        {
            for (int i = 0; i < sprites.Length; i++)
            {
                Issue30 s = sprites[i];
                if (s != null && s.Collider == null)
                {
                    s.Collider = this;
                    Cover = s;
                    return true;
                }
            }
            return false;
        }
    }
}
