using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Compiles a marker's own text to IL: Rewind writes what it couldn't express in IL as an expression
// ("__store8((t0 + 40L), ((1d - _value) * 0.6094d))"), and with a few facts from the rule (t0 is the
// double[] in local 0; t2 is local 2) that expression is the repair. The game's logic then comes
// from the user's Rewind output at build time, and the rule holds only the facts.
//
// The language, as far as it is supported: integer literals (8, 8L), double literals (1d, 0.3086d),
// parameters by name, this, temporaries bound by the rule (tN, mN), parentheses, unary -,
// * / + - << >> >>> & | ^, conv.i4/u4/i8/u8/r8(e), __load8/__load4(addr), __store8/__store4(addr, v),
// __bits_to_double(e), where addr is (t + offset) for a temporary bound to an array.
// Anything else is an error naming the marker, and the rule writes that site's IL by hand.
namespace Repairs
{
    public enum Kind { I4, I8, R8, Ref }

    public sealed class ExprCompiler
    {
        readonly MethodRepair repair;
        readonly Marker marker;
        readonly IL il;
        string text;
        int pos;

        // Arrays: array header size on 64-bit IL2CPP (klass, monitor, bounds, length + padding).
        public const int ArrayHeader = 32;

        ExprCompiler(MethodRepair repair, Marker marker, IL il)
        {
            this.repair = repair;
            this.marker = marker;
            this.il = il;
        }

        public static void Compile(MethodRepair repair, Marker marker, IL il)
        {
            var c = new ExprCompiler(repair, marker, il);
            c.text = marker.Text;
            c.pos = 0;
            Node n = c.ParseExpr();
            c.Skip();
            if (c.pos != c.text.Length) throw c.Error("unexpected text at " + c.pos + ": " + c.text.Substring(c.pos));
            if (marker.IsValue)
            {
                Kind want = KindOf(marker.ValueType);
                Kind got = n.Emit(c, want);
                c.Convert(got, want);
            }
            else
            {
                Kind got = n.Emit(c, null);
                if (got != Kind.Ref || !(n is Call && ((Call)n).IsStore)) throw c.Error("an Effect must be a store");
            }
        }

        static Kind KindOf(TypeReference t)
        {
            switch (t.FullName)
            {
                case "System.Int32": case "System.Boolean": case "System.Byte": return Kind.I4;
                case "System.Int64": case "System.UInt64": return Kind.I8;
                case "System.Double": return Kind.R8;
            }
            return Kind.Ref;
        }

        RepairException Error(string what)
        {
            return new RepairException(repair.Key + ": marker " + marker.Index + " (" + marker.Text + "): " + what);
        }

        // --- conversions ---

        void Convert(Kind from, Kind? to)
        {
            if (to == null || from == to.Value) return;
            if (from == Kind.R8 && to.Value == Kind.I8)
            {
                // a double read where Rewind wants its bits (it keeps doubles in int64 temporaries)
                il.Call("System.BitConverter::DoubleToInt64Bits(Double)");
                return;
            }
            if (from == Kind.I8 && to.Value == Kind.R8) throw Error("an int64 where a double is wanted: say __bits_to_double or conv.r8");
            if (to.Value == Kind.I4) il.Op(OpCodes.Conv_I4);
            else if (to.Value == Kind.I8) il.Op(OpCodes.Conv_I8);
            else if (to.Value == Kind.R8) il.Op(OpCodes.Conv_R8);
            else throw Error("cannot convert " + from + " to " + to);
        }

        // --- parsing ---

        abstract class Node
        {
            // Emits the value; want is a hint (literals take it); returns the kind pushed.
            public abstract Kind Emit(ExprCompiler c, Kind? want);
        }

        sealed class Num : Node
        {
            public string Text;
            public override Kind Emit(ExprCompiler c, Kind? want)
            {
                if (Text.EndsWith("d"))
                {
                    c.il.R8(double.Parse(Text.TrimEnd('d'), CultureInfo.InvariantCulture));
                    return Kind.R8;
                }
                long v = long.Parse(Text.TrimEnd('L'), CultureInfo.InvariantCulture);
                if (want == Kind.R8 && Text.EndsWith("L"))
                {
                    // a double's bits written as an int64 literal (a constant Rewind moved through rax)
                    c.il.R8(BitConverter.Int64BitsToDouble(v));
                    return Kind.R8;
                }
                if (Text.EndsWith("L") || want == Kind.I8 || v > int.MaxValue || v < int.MinValue)
                {
                    c.il.I8(v);
                    return Kind.I8;
                }
                c.il.I4((int)v);
                return Kind.I4;
            }
        }

        sealed class Name : Node
        {
            public string Id;
            public override Kind Emit(ExprCompiler c, Kind? want)
            {
                if (Id == "this")
                {
                    c.il.This();
                    return Kind.Ref;
                }
                ParameterDefinition p = c.repair.Method.Parameters.FirstOrDefault(x => x.Name == Id);
                if (p != null)
                {
                    c.il.Emit(Instruction.Create(OpCodes.Ldarg, p));
                    return KindOf(p.ParameterType);
                }
                VariableDefinition v = c.repair.Bound(Id);
                if (v == null) throw c.Error("no binding for " + Id);
                c.il.Ldloc(v);
                return KindOf(v.VariableType);
            }
        }

        sealed class Unary : Node
        {
            public Node Operand;
            public override Kind Emit(ExprCompiler c, Kind? want)
            {
                Kind k = Operand.Emit(c, want);
                c.il.Op(OpCodes.Neg);
                return k;
            }
        }

        sealed class Binary : Node
        {
            public string Op;
            public Node Left, Right;
            public override Kind Emit(ExprCompiler c, Kind? want)
            {
                bool shift = Op == "<<" || Op == ">>" || Op == ">>>";
                Kind k;
                if (shift)
                {
                    // the shifted value keeps its kind; the count is an int32
                    k = Left.Emit(c, want);
                    Kind r = Right.Emit(c, Kind.I4);
                    if (r != Kind.I4) c.Convert(r, Kind.I4);
                }
                else
                {
                    // the wider of the two decides, as in C#
                    k = Wider(c.Probe(Left, want), c.Probe(Right, want));
                    c.Convert(Left.Emit(c, k), k);
                    c.Convert(Right.Emit(c, k), k);
                }
                switch (Op)
                {
                    case "+": c.il.Op(OpCodes.Add); break;
                    case "-": c.il.Op(OpCodes.Sub); break;
                    case "*": c.il.Op(OpCodes.Mul); break;
                    case "/": c.il.Op(OpCodes.Div); break;
                    case "&": c.il.Op(OpCodes.And); break;
                    case "|": c.il.Op(OpCodes.Or); break;
                    case "^": c.il.Op(OpCodes.Xor); break;
                    case "<<": c.il.Op(OpCodes.Shl); break;
                    case ">>": c.il.Op(OpCodes.Shr); break;
                    case ">>>": c.il.Op(OpCodes.Shr_Un); break;
                    default: throw c.Error("operator " + Op);
                }
                return k;
            }

            static Kind Wider(Kind a, Kind b)
            {
                if (a == Kind.R8 || b == Kind.R8) return Kind.R8;
                if (a == Kind.I8 || b == Kind.I8) return Kind.I8;
                return Kind.I4;
            }
        }

        sealed class Call : Node
        {
            public string Fn;
            public List<Node> Args;
            public bool IsStore { get { return Fn == "__store8" || Fn == "__store4"; } }

            public override Kind Emit(ExprCompiler c, Kind? want)
            {
                switch (Fn)
                {
                    case "conv.i4": c.Convert(Args[0].Emit(c, null), Kind.I4); return Kind.I4;
                    case "conv.u4": Args[0].Emit(c, null); c.il.Op(OpCodes.Conv_U4); return Kind.I4;
                    case "conv.i8": Args[0].Emit(c, null); c.il.Op(OpCodes.Conv_I8); return Kind.I8;
                    case "conv.u8": Args[0].Emit(c, null); c.il.Op(OpCodes.Conv_U8); return Kind.I8;
                    case "conv.r8": c.Convert(Args[0].Emit(c, Kind.R8), Kind.R8); return Kind.R8;
                    case "__bits_to_double":
                    {
                        Kind k = Args[0].Emit(c, Kind.I8);
                        if (k == Kind.R8) return Kind.R8;          // a load from a double[] is the double already
                        c.Convert(k, Kind.I8);
                        c.il.BitsToDouble();
                        return Kind.R8;
                    }
                    case "__load8":
                    case "__load4":
                    {
                        TypeReference elem = c.Element(Args[0], Fn == "__load8" ? 8 : 4);
                        c.il.Emit(Instruction.Create(OpCodes.Ldelem_Any, c.repair.Engine.Module.ImportReference(elem)));
                        return KindOf(elem);
                    }
                    case "__store8":
                    case "__store4":
                    {
                        TypeReference elem = c.Element(Args[0], Fn == "__store8" ? 8 : 4);
                        Kind k = KindOf(elem);
                        c.Convert(Args[1].Emit(c, k), k);
                        c.il.Emit(Instruction.Create(OpCodes.Stelem_Any, c.repair.Engine.Module.ImportReference(elem)));
                        return Kind.Ref;
                    }
                }
                throw c.Error("function " + Fn + " is not supported");
            }
        }

        // An address (t + offset) into a bound array: pushes the array and the index; the element type.
        TypeReference Element(Node addr, int size)
        {
            var b = addr as Binary;
            Name t = b != null && b.Op == "+" ? b.Left as Name : addr as Name;
            Num off = b != null && b.Op == "+" ? b.Right as Num : null;
            long offset = off == null ? 0 : long.Parse(off.Text.TrimEnd('L'), CultureInfo.InvariantCulture);
            VariableDefinition v = t == null ? null : repair.Bound(t.Id);
            var array = v == null ? null : v.VariableType as ArrayType;
            if (array == null) throw Error("address " + (t == null ? "?" : t.Id) + " is not bound to an array local");
            int elemSize = ElementSize(array.ElementType);
            if (elemSize != size) throw Error("a " + size + "-byte access to a " + array.ElementType.Name + "[]");
            if ((offset - ArrayHeader) % elemSize != 0 || offset < ArrayHeader) throw Error("offset " + offset + " is not an element");
            il.Ldloc(v).I4((int)((offset - ArrayHeader) / elemSize));
            return array.ElementType;
        }

        static int ElementSize(TypeReference t)
        {
            switch (t.FullName)
            {
                case "System.Double": case "System.Int64": return 8;
                case "System.Int32": case "System.Single": return 4;
            }
            return 8;  // references on x64
        }

        // The kind an expression would push, without emitting it.
        Kind Probe(Node n, Kind? want)
        {
            int mark = il.Instructions.Count;
            Kind k = n.Emit(this, want);
            il.Instructions.RemoveRange(mark, il.Instructions.Count - mark);
            return k;
        }

        void Skip()
        {
            while (pos < text.Length && text[pos] == ' ') pos++;
        }

        bool Eat(string s)
        {
            Skip();
            if (string.CompareOrdinal(text, pos, s, 0, s.Length) != 0) return false;
            // don't take '>>' out of '>>>', or '<' out of '<<'
            if ((s == ">>" && pos + 2 < text.Length && text[pos + 2] == '>')) return false;
            pos += s.Length;
            return true;
        }

        static readonly string[][] levels =
        {
            new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { ">>>", "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/" },
        };

        Node ParseExpr() { return ParseLevel(0); }

        Node ParseLevel(int level)
        {
            if (level == levels.Length) return ParseUnary();
            Node left = ParseLevel(level + 1);
            for (;;)
            {
                string op = levels[level].FirstOrDefault(Eat);
                if (op == null) return left;
                left = new Binary { Op = op, Left = left, Right = ParseLevel(level + 1) };
            }
        }

        Node ParseUnary()
        {
            Skip();
            if (Eat("-"))
            {
                Skip();
                if (pos < text.Length && char.IsDigit(text[pos])) return ParseNumber("-");
                return new Unary { Operand = ParseUnary() };
            }
            if (Eat("("))
            {
                Node inner = ParseExpr();
                if (!Eat(")")) throw Error("missing ) at " + pos);
                return inner;
            }
            if (pos < text.Length && char.IsDigit(text[pos])) return ParseNumber("");
            int start = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_' || text[pos] == '.')) pos++;
            string id = text.Substring(start, pos - start);
            if (id.Length == 0) throw Error("unexpected '" + text[pos] + "' at " + pos);
            if (Eat("("))
            {
                var args = new List<Node>();
                if (!Eat(")"))
                {
                    do args.Add(ParseExpr()); while (Eat(","));
                    if (!Eat(")")) throw Error("missing ) at " + pos);
                }
                return new Call { Fn = id, Args = args };
            }
            return new Name { Id = id };
        }

        Node ParseNumber(string sign)
        {
            int start = pos;
            while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.' || text[pos] == 'E' || text[pos] == 'e'
                                         || ((text[pos] == '-' || text[pos] == '+') && (text[pos - 1] == 'E' || text[pos - 1] == 'e')))) pos++;
            if (pos < text.Length && (text[pos] == 'd' || text[pos] == 'L')) pos++;
            return new Num { Text = sign + text.Substring(start, pos - start) };
        }
    }
}
