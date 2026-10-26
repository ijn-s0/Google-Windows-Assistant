using System;
using System.Globalization;

namespace GeminiLauncher
{
    /// <summary>Inline calculator: + - * / % ^, parentheses, sqrt/sin/cos/tan/log/ln/abs, pi, e.</summary>
    public static class Calc
    {
        public static bool TryEval(string input, out string result)
        {
            result = null;
            var s = (input ?? "").Trim().TrimEnd('=').Replace("×", "*").Replace("÷", "/").Replace(",", "");
            if (s.Length < 2) return false;
            bool hasOp = false, hasDigit = false;
            foreach (char c in s)
            {
                if ("+-*/^%()".IndexOf(c) >= 0) hasOp = true;
                if (char.IsDigit(c)) hasDigit = true;
            }
            if (!hasDigit || (!hasOp && !s.StartsWith("sqrt"))) return false;
            try
            {
                int i = 0;
                double v = Expr(s, ref i);
                Skip(s, ref i);
                if (i != s.Length || double.IsNaN(v) || double.IsInfinity(v)) return false;
                if (s.Trim('(', ')', ' ') == v.ToString(CultureInfo.InvariantCulture)) return false;
                result = Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0)
                    ? v.ToString("G10", CultureInfo.InvariantCulture)
                    : Math.Round(v, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);
                return true;
            }
            catch { return false; }
        }

        static void Skip(string s, ref int i) { while (i < s.Length && s[i] == ' ') i++; }

        static double Expr(string s, ref int i)
        {
            double v = Term(s, ref i);
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && s[i] == '+') { i++; v += Term(s, ref i); }
                else if (i < s.Length && s[i] == '-') { i++; v -= Term(s, ref i); }
                else return v;
            }
        }

        static double Term(string s, ref int i)
        {
            double v = Power(s, ref i);
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && (s[i] == '*' || s[i] == 'x')) { i++; v *= Power(s, ref i); }
                else if (i < s.Length && s[i] == '/') { i++; v /= Power(s, ref i); }
                else if (i < s.Length && s[i] == '%')
                {
                    i++;
                    Skip(s, ref i);
                    // "50%" alone = 0.5 ; "10 % 3" = modulo
                    if (i < s.Length && (char.IsDigit(s[i]) || s[i] == '(' || s[i] == '.')) v %= Power(s, ref i);
                    else v /= 100;
                }
                else return v;
            }
        }

        static double Power(string s, ref int i)
        {
            double b = Unary(s, ref i);
            Skip(s, ref i);
            if (i < s.Length && s[i] == '^') { i++; return Math.Pow(b, Power(s, ref i)); }
            return b;
        }

        static double Unary(string s, ref int i)
        {
            Skip(s, ref i);
            if (i < s.Length && s[i] == '-') { i++; return -Unary(s, ref i); }
            if (i < s.Length && s[i] == '+') { i++; return Unary(s, ref i); }
            return Atom(s, ref i);
        }

        static double Atom(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException();
            if (s[i] == '(')
            {
                i++;
                double v = Expr(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ')') i++;
                return v;
            }
            if (char.IsLetter(s[i]))
            {
                int st = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                var name = s.Substring(st, i - st).ToLowerInvariant();
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                double a = Atom(s, ref i);
                switch (name)
                {
                    case "sqrt": return Math.Sqrt(a);
                    case "sin": return Math.Sin(a);
                    case "cos": return Math.Cos(a);
                    case "tan": return Math.Tan(a);
                    case "log": return Math.Log10(a);
                    case "ln": return Math.Log(a);
                    case "abs": return Math.Abs(a);
                    default: throw new FormatException();
                }
            }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i) throw new FormatException();
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }
    }
}
