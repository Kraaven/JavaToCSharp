using System.Text;
using System.Text.RegularExpressions;

namespace JavaToCSharp;

public static partial class TypeNameParser
{
    private enum TokenType
    {
        EndOfString = 0,
        Identifier,
        Extends,
        Super,
        LeftSquareBracket,
        RightSquareBracket,
        LeftAngleBracket,
        RightAngleBracket,
        Comma,
        QuestionMark
    }

    [GeneratedRegex(@"\w+|\[|\]|<|>|,|\?", RegexOptions.Compiled)]
    private static partial Regex TokenizePattern { get; }

    internal static string ParseTypeName(string typename, Func<string, string> translateIdentifier)
    {
        // Example: List<Pair<Integer, ? extends Person>>
        //
        // EBNF:
        // TypeName = identifier [ "<" TypeArgument { "," TypeArgument } ">" ].
        // TypeArgument = [ "?" [ "extends" | "super" ] ] TypeName.

        var parser = new Parser(Tokenize(typename), translateIdentifier);
        return parser.TryParse() ?? typename;
    }

    private static (string text, TokenType type)[] Tokenize(string typeName)
    {
        var matches = TokenizePattern.Matches(typeName);
        var tokens = new (string text, TokenType type)[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            string s = matches[i].Value;
            tokens[i] = s switch
            {
                "[" => (s, TokenType.LeftSquareBracket),
                "]" => (s, TokenType.RightSquareBracket),
                "<" => (s, TokenType.LeftAngleBracket),
                ">" => (s, TokenType.RightAngleBracket),
                "," => (s, TokenType.Comma),
                "?" => (s, TokenType.QuestionMark),
                "extends" => (s, TokenType.Extends),
                "super" => (s, TokenType.Super),
                _ => (s, TokenType.Identifier)
            };
        }
        return tokens;
    }

    private sealed class Parser
    {
        private readonly (string text, TokenType type)[] _tokens;
        private readonly Func<string, string> _translate;
        private readonly StringBuilder _sb = new();
        private int _currentIndex = -1;
        private (string text, TokenType type) _token;

        public Parser((string text, TokenType type)[] tokens, Func<string, string> translate)
        {
            _tokens = tokens;
            _translate = translate;
        }

        public string? TryParse()
        {
            NextToken();
            if (TypeName() && _token.type is TokenType.EndOfString)
            {
                return _sb.ToString();
            }

            return null;
        }

        private void NextToken()
        {
            _currentIndex++;
            _token = _currentIndex < _tokens.Length ? _tokens[_currentIndex] : default;
        }

        private bool TypeName()
        {
            // TypeName = identifier [ "<" TypeArgument { "," TypeArgument } ">" ].
            if (_token.type is TokenType.Identifier)
            {
                _sb.Append(_translate(_token.text));
                NextToken();
                if (_token.type is TokenType.LeftAngleBracket)
                {
                    _sb.Append('<');
                    NextToken();
                    if (TypeArgument())
                    {
                        while (_token.type is TokenType.Comma)
                        {
                            _sb.Append(", ");
                            NextToken();
                            if (!TypeArgument()) return false;
                        }
                        if (_token.type is TokenType.RightAngleBracket)
                        {
                            _sb.Append('>');
                            NextToken();
                            return true;
                        }
                    }
                }
                else
                {
                    return true;
                }
            }
            return false;
        }

        private bool TypeArgument()
        {
            // TypeArgument = [ "?" [ "extends" | "super" ] ] TypeName.
            if (_token.type is TokenType.QuestionMark)
            {
                _sb.Append("TWildcardTodo");
                NextToken();

                if (_token.type is TokenType.Extends or TokenType.Super)
                {
                    NextToken();
                }
                else if (_token.type is TokenType.RightAngleBracket)
                {
                    return true;
                }
            }
            return TypeName();
        }
    }
}
