// A test assembly for building Rewind signature libraries: it makes IL2CPP emit calls to as many of its runtime
// helpers as possible (array allocation and stores, invokers, metadata init, boxing, casts, exceptions, delegates,
// strings, P/Invoke, internal calls, math). Nothing here comes from a game. The behaviour of the code doesn't
// matter; only that IL2CPP generates the helper calls a real game's code would.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SigTest
{
    public interface IShape
    {
        double Area();
        void Scale(double factor);
        T Visit<T>(IVisitor<T> visitor);
    }

    public interface IVisitor<T>
    {
        T VisitCircle(Circle c);
        T VisitRect(Rect r);
    }

    public abstract class Shape : IShape, IComparable<Shape>
    {
        public static int Created;
        public string Name;
        protected Shape(string name) { Name = name; Created++; }
        public abstract double Area();
        public virtual void Scale(double factor) { }
        public abstract T Visit<T>(IVisitor<T> visitor);
        public virtual U Convert<U>(Func<Shape, U> f) => f(this);
        public int CompareTo(Shape other) => Area().CompareTo(other.Area());
        public override string ToString() => Name + ":" + Area().ToString("F2");
    }

    public sealed class Circle : Shape
    {
        public double R;
        public Circle() : this(1) { }
        public Circle(double r) : base("circle") { R = r; }
        public override double Area() => Math.PI * R * R;
        public override void Scale(double factor) { R *= factor; }
        public override T Visit<T>(IVisitor<T> visitor) => visitor.VisitCircle(this);
    }

    public class Rect : Shape
    {
        public double W, H;
        public Rect() : this(1, 1) { }
        public Rect(double w, double h) : base("rect") { W = w; H = h; }
        public override double Area() => W * H;
        public override void Scale(double factor) { W *= factor; H *= factor; }
        public override T Visit<T>(IVisitor<T> visitor) => visitor.VisitRect(this);
        public override U Convert<U>(Func<Shape, U> f) => base.Convert(f);
    }

    public sealed class PerimeterVisitor : IVisitor<double>
    {
        public double VisitCircle(Circle c) => 2 * Math.PI * c.R;
        public double VisitRect(Rect r) => 2 * (r.W + r.H);
    }

    public struct Point : IEquatable<Point>, IFormattable
    {
        public int X, Y;
        public Point(int x, int y) { X = x; Y = y; }
        public bool Equals(Point other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Point p && Equals(p);
        public override int GetHashCode() => X * 31 + Y;
        public string ToString(string format, IFormatProvider provider) => X.ToString(format, provider) + "," + Y.ToString(format, provider);
    }

    public struct Big
    {
        public long A, B, C, D, E, F, G, H;
        public Vector3 V;
        public string S;
    }

    public class Box<T>
    {
        public T Value;
        public T[] Items = new T[4];
        public static Box<T> Default = new Box<T>();
        public virtual T Get() => Value;
        public void Set(T value) { Value = value; Items[0] = value; }
        public bool Same(T other) => EqualityComparer<T>.Default.Equals(Value, other);
        public string Describe() => Value.ToString() + Value.GetHashCode();
        public int CompareWith<TC>(TC other) where TC : IComparable<T> => other.CompareTo(Value);
    }

    public class Node<T> where T : class
    {
        public T Item;
        public Node<T> Next;
        public Node(T item, Node<T> next) { Item = item; Next = next; }
    }

    public delegate double Op(double a, double b);

    public sealed class Resource : IDisposable
    {
        public static int Open;
        public Resource() { Interlocked.Increment(ref Open); }
        public void Dispose() { Interlocked.Decrement(ref Open); }
    }

    public static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetTickCount();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetEnvironmentVariableW(string name, StringBuilder buffer, int size);
    }

    public static class Tables
    {
        public static readonly double[] Weights = { 0.5, 1.25, -3.75, 1e10, double.Epsilon, 7.0, 8.5, 9.25 };
        public static readonly int[] Ids = { 1, 2, 3, 5, 8, 13, 21, 34 };
        public static readonly string[] Names = { "a", "b", "c" };
        public static readonly Vector2[] Corners = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1) };
        public static readonly Dictionary<string, int> Index = new Dictionary<string, int>();
        public static double[] Fill(int n, double seed)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = seed * i - 0.5;
            a[0] = 1.5; a[1] = -2.25; a[n - 1] = 3.0e-3;
            return a;
        }
        public static object[][] Jagged(int n)
        {
            var outer = new object[n][];
            for (int i = 0; i < n; i++) { outer[i] = new object[i + 1]; outer[i][0] = i; }
            return outer;
        }
        public static int[,] Grid(int w, int h)
        {
            var g = new int[w, h];
            for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) g[x, y] = x ^ y;
            return g;
        }
        public static void Covariant(object[] target, object value) { target[0] = value; }
    }

    public static class Work
    {
        [ThreadStatic] public static int PerThread;
        public static event Action<string> Logged;
        static readonly object Gate = new object();

        public static string Strings(string s, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) if (char.IsDigit(s[i])) sb.Append(s[i]);
            string t = string.Concat(s, "/", n.ToString(), "/", sb.ToString());
            switch (t.Substring(0, Math.Min(3, t.Length)))
            {
                case "abc": t += "1"; break;
                case "def": t += "2"; break;
                case "ghi": t += "3"; break;
                case "jkl": t += "4"; break;
                case "mno": t += "5"; break;
                case "pqr": t += "6"; break;
                case "stu": t += "7"; break;
            }
            Logged?.Invoke(t);
            return string.Format("{0}-{1}-{2}", t, n, s.ToUpperInvariant()) + t.IndexOf('/') + t.Split('/').Length;
        }

        public static double Maths(double x, float y)
        {
            double r = Math.Sin(x) + Math.Cos(x) * Math.Tan(x) + Math.Pow(x, 3) + Math.Sqrt(Math.Abs(x)) + Math.Atan2(x, 2)
                       + Math.Floor(x) + Math.Ceiling(x) + Math.Round(x) + Math.Log(x + 10) + Math.Exp(x / 10) + (x % 3.5)
                       + Math.Asin(0.5) + Math.Acos(0.25) + Math.Atan(x) + Math.Log10(x + 20) + Math.Sinh(0.1) + Math.Cosh(0.1) + Math.Tanh(0.1);
            float f = Mathf.Sin(y) + Mathf.Cos(y) + Mathf.Sqrt(y) + Mathf.Pow(y, 2) + (y % 1.5f) + Mathf.Atan2(y, 1) + MathF.Floor(y) + MathF.Round(y);
            r += -x;
            return r + f + (double)(long)x + (double)(ulong)y + (int)r + (uint)f + (long)(r * 1000) + checked((int)y * 3);
        }

        public static int Exceptions(object o, int i)
        {
            int result = 0;
            try
            {
                if (o == null) throw new ArgumentNullException(nameof(o));
                if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
                if (i == 3) throw new NotSupportedException();
                result = checked(i * 1000000 * 1000);
                var s = (string)o;
                result += s.Length;
                var arr = new object[1] as string[];
            }
            catch (OverflowException e) { result = -1; Debug.Log(e.Message); }
            catch (InvalidCastException) { result = -2; }
            catch (Exception e) when (e is ArgumentException) { result = -3; }
            finally { PerThread++; }
            using (var r = new Resource())
            {
                try { result += Tables.Ids[i]; }
                catch (IndexOutOfRangeException) { result += 100; }
            }
            lock (Gate) { result ^= PerThread; }
            return result;
        }

        public static object Boxing(int i, double d, Point p, int? n, Vector3 v)
        {
            object a = i, b = d, c = p, e = n, f = v;
            object[] all = { a, b, c, e, f, (long)i, (byte)i, 'x', true, DayOfWeek.Friday };
            int back = (int)a + (int)(double)b + ((Point)c).X + ((int?)e ?? 0) + (int)((Vector3)f).x;
            if (all[2] is Point q) back += q.Y;
            if (all[3] is int k) back += k;
            int? m = all[3] as int?;
            IFormattable fmt = p;
            IEquatable<Point> eq = p;
            back += eq.Equals(p) ? 1 : 0;
            return fmt.ToString("D", null) + back + m.GetValueOrDefault() + Nullable.Compare(n, m);
        }

        public static double Casts(object o, IShape s, Shape[] shapes)
        {
            double total = 0;
            if (o is Circle c) total += c.R;
            if (o is Rect r) total += r.W;
            if (o is IDisposable dd) dd.Dispose();
            var sh = o as Shape;
            if (sh != null) total += sh.Area();
            total += ((IShape)o).Area();
            total += ((Rect)s).H;
            object[] objs = shapes;
            total += objs.Length + ((Shape)objs[0]).Area();
            IList<Shape> list = shapes;
            total += list.Count;
            Type t = o.GetType();
            if (t == typeof(Circle) || typeof(Shape).IsAssignableFrom(t)) total += 1;
            return total;
        }

        public static double Generics(List<Shape> shapes, Dictionary<string, double> map)
        {
            double total = 0;
            foreach (var s in shapes) { total += s.Area(); s.Scale(1.1); }
            shapes.Sort();
            shapes.Sort((a, b) => a.Name.CompareTo(b.Name));
            var visitor = new PerimeterVisitor();
            for (int i = 0; i < shapes.Count; i++)
            {
                total += shapes[i].Visit(visitor);
                total += shapes[i].Visit<double>(visitor);
                total += shapes[i].Convert(x => x.Area());
                total += shapes[i].Convert<string>(x => x.Name).Length;
                IShape ish = shapes[i];
                total += ish.Visit(visitor);
            }
            foreach (KeyValuePair<string, double> kv in map) total += kv.Value + kv.Key.Length;
            if (map.TryGetValue("x", out double v)) total += v;
            var ints = new Box<int>(); ints.Set(5); total += ints.Get() + (ints.Same(5) ? 1 : 0) + ints.Describe().Length + ints.CompareWith(3);
            var dbl = new Box<double>(); dbl.Set(2.5); total += dbl.Get() + dbl.Describe().Length;
            var strs = new Box<string>(); strs.Set("q"); total += strs.Get().Length + strs.Describe().Length + (strs.Same("q") ? 1 : 0);
            var vec = new Box<Vector3>(); vec.Set(Vector3.up); total += vec.Get().y + vec.Describe().Length;
            var pts = new Box<Point>(); pts.Set(new Point(1, 2)); total += pts.Get().X + (pts.Same(new Point(1, 2)) ? 1 : 0);
            total += Box<long>.Default.Items.Length + Box<object>.Default.Items.Length;
            var node = new Node<string>("a", new Node<string>("b", null));
            for (var n = node; n != null; n = n.Next) total += n.Item.Length;
            total += Sum<int>(new List<int> { 1, 2, 3 }) + Sum<double>(new List<double> { 1.5 }) + Sum<string>(new List<string> { "s" });
            total += Max(1, 2) + Max(1.5, 0.5) + Max("a", "b").Length + Max(new Point(1, 1), new Point(2, 2)).X;
            var q = new Queue<Vector2>(); q.Enqueue(Vector2.one); total += q.Dequeue().x;
            var hs = new HashSet<int> { 1, 2 }; total += hs.Count;
            var stack = new Stack<object>(); stack.Push(1); total += stack.Count;
            return total + Created<Rect>().W + Created<Circle>().R;
        }

        static int Sum<T>(IEnumerable<T> items)
        {
            int count = 0;
            foreach (T item in items) count += item.GetHashCode() + item.ToString().Length;
            return count;
        }

        static T Max<T>(T a, T b) where T : IEquatable<T> => a.Equals(b) ? a : (a.GetHashCode() > b.GetHashCode() ? a : b);
        static T Created<T>() where T : Shape, new() => new T();

        public static IEnumerable<int> Iterate(int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (i % 7 == 3) yield break;
                yield return i * i;
            }
        }

        public static double Delegates(double x)
        {
            Op add = (a, b) => a + b;
            Op mul = delegate (double a, double b) { return a * b; };
            Op both = add + mul;
            double captured = x;
            Func<double, double> f = y => y * captured;
            Action<int> act = i => captured += i;
            act(3);
            Func<int, string> g = i => i.ToString();
            Predicate<Shape> p = s => s.Area() > 1;
            Comparison<int> cmp = (a, b) => b - a;
            Func<Shape, double> area = Shape.ReferenceEquals(f, null) ? null : (Func<Shape, double>)(s => s.Area());
            var list = new List<int> { 3, 1, 2 };
            list.Sort(cmp);
            list.RemoveAll(i => i > 2);
            double r = add(x, 1) + mul(x, 2) + both(1, 2) + f(2) + g(5).Length + (p(new Circle(1)) ? 1 : 0) + list.Count;
            r += area?.Invoke(new Rect(1, 2)) ?? 0;
            both -= add;
            r += both.Invoke(2, 3) + both.GetInvocationList().Length;
            foreach (int i in Iterate(10)) r += i;
            return r;
        }

        public static Vector3 Structs(Vector3 a, Vector2 b, Big big, ref Big outBig)
        {
            Vector3 c = a + new Vector3(b.x, b.y, 0) * 2f - Vector3.Cross(a, Vector3.forward);
            c = Vector3.Normalize(c) + Vector3.Lerp(a, c, 0.5f) + Quaternion.Euler(0, 90, 0) * a;
            Vector2 d = b.normalized * b.magnitude + Vector2.Perpendicular(b);
            var arr = new Vector3[3];
            arr[0] = c; arr[1] = a; arr[2] = arr[0] + arr[1];
            big.V = c; big.A += 1; big.S = "big";
            outBig = big;
            Big copy = default(Big);
            copy.H = big.H;
            outBig.G = copy.H;
            Color col = new Color(0.1f, 0.2f, 0.3f, 1f) * 2f;
            Rect r = null;
            var bounds = new Bounds(c, Vector3.one);
            Matrix4x4 m = Matrix4x4.TRS(a, Quaternion.identity, Vector3.one);
            return arr[2] + new Vector3(d.x, d.y, col.r) + m.MultiplyPoint(a) + bounds.center + (r == null ? Vector3.zero : Vector3.one);
        }

        public static unsafe int Unsafe(int n)
        {
            int* buffer = stackalloc int[Math.Max(n, 1)];
            for (int i = 0; i < n; i++) buffer[i] = i;
            var bytes = new byte[n * 4 + 4];
            fixed (byte* p = bytes) { *(int*)p = n; }
            Buffer.BlockCopy(bytes, 0, new byte[bytes.Length], 0, bytes.Length);
            Array.Copy(Tables.Ids, new int[8], 8);
            var sb = new StringBuilder(64);
            int got = Native.GetEnvironmentVariableW("PATH", sb, 64);
            int error = Marshal.GetLastWin32Error();
            return (int)Native.GetTickCount() + got + error + buffer[0] + bytes.Length + sizeof(Vector3);
        }

        public static long Statics()
        {
            Shape.Created += 1;
            Tables.Index["k"] = Tables.Ids.Length;
            long acc = Shape.Created + Tables.Weights.Length + Tables.Names.Length + Tables.Corners.Length + PerThread;
            acc += Interlocked.Add(ref Shape.Created, 2);
            acc += Volatile.Read(ref Resource.Open);
            acc += Environment.TickCount;
            return acc;
        }
    }

    public class Driver : MonoBehaviour
    {
        public float Speed = 2f;
        public Transform Target;
        public Vector3[] Path = new Vector3[2];
        List<Shape> shapes = new List<Shape>();
        Dictionary<string, double> map = new Dictionary<string, double>();
        double[] weights;
        Big big;

        void Awake()
        {
            shapes.Add(new Circle(1)); shapes.Add(new Rect(2, 3));
            map["x"] = 1.5;
            weights = Tables.Fill(16, 0.25);
            if (BigTables.All(4096) == 0) Debug.Log("empty");
            Work.Logged += s => Debug.Log(s);
        }

        void Start()
        {
            Debug.Log(Work.Strings("abc123", shapes.Count));
            Debug.Log(Work.Boxing(1, 2.5, new Point(3, 4), 5, Vector3.one));
            StartCoroutine(Wait());
        }

        System.Collections.IEnumerator Wait()
        {
            yield return new WaitForSeconds(0.5f);
            Debug.LogWarning("waited");
        }

        void Update()
        {
            float dt = Time.deltaTime;
            Vector3 pos = transform.position;
            pos += Vector3.right * Speed * dt;
            if (Target != null) pos = Vector3.MoveTowards(pos, Target.position, dt);
            transform.position = pos;
            double r = Work.Maths(pos.x, dt) + Work.Casts(shapes[0], shapes[1], shapes.ToArray()) + Work.Generics(shapes, map) + Work.Delegates(dt);
            r += Work.Exceptions(Time.frameCount % 2 == 0 ? (object)"s" : 5, Time.frameCount % 9) + Work.Unsafe(4) + Work.Statics();
            Path[0] = Work.Structs(pos, new Vector2(dt, 1), big, ref big);
            weights[Time.frameCount % weights.Length] = r;
            Tables.Covariant(new string[1], "s");
            var grid = Tables.Grid(3, 4);
            var jag = Tables.Jagged(3);
            if (Input.GetKeyDown(KeyCode.Space)) GetComponent<Renderer>().enabled = !GetComponent<Renderer>().enabled;
            if (r > 1e300) Destroy(gameObject);
            gameObject.name = grid[1, 2].ToString() + jag[2][0];
        }

        void OnDestroy() { shapes.Clear(); }
    }
}
