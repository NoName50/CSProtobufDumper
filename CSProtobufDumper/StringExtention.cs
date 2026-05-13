using System.Linq;
using System.Text;

public static class StringExtention 
{
    private static readonly StringBuilder _stringBuilder = new StringBuilder();

    public static string PascalToSnake(this string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length == 1 || s.All(char.IsLower) || s.All(char.IsUpper)) return s;

        _stringBuilder.Clear();
        _stringBuilder.EnsureCapacity(s.Length - 1);
        _stringBuilder.Append(char.ToLowerInvariant(s[0]));

        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c))
            {
                _stringBuilder.Append('_');
                _stringBuilder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                _stringBuilder.Append(c);
            }
        }

        return _stringBuilder.ToString();
    }
}