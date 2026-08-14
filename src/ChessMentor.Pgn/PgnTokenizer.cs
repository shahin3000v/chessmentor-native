namespace ChessMentor.Pgn;

internal sealed class PgnTokenizer
{
    private static readonly string[] Results = ["1/2-1/2", "1-0", "0-1", "*"];
    private readonly List<PgnToken> _tokens = [];
    private readonly List<PgnDiagnostic> _diagnostics = [];
    private string _source = string.Empty;
    private int _index;
    private int _line;
    private int _column;

    public (IReadOnlyList<PgnToken> Tokens, IReadOnlyList<PgnDiagnostic> Diagnostics) Tokenize(
        string source,
        CancellationToken cancellationToken)
    {
        _source = source ?? string.Empty;
        _index = 0;
        _line = 1;
        _column = 1;
        _tokens.Clear();
        _diagnostics.Clear();

        while (_index < _source.Length)
        {
            if ((_index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var ch = _source[_index];
            if (char.IsWhiteSpace(ch))
            {
                ReadWhile(PgnTokenKind.Whitespace, static value => char.IsWhiteSpace(value));
            }
            else if (ch == '[')
            {
                ReadHeader();
            }
            else if (ch == '{')
            {
                ReadBraceComment();
            }
            else if (ch == ';')
            {
                ReadLineComment();
            }
            else if (ch == '(')
            {
                AddSingle(PgnTokenKind.VariationStart);
            }
            else if (ch == ')')
            {
                AddSingle(PgnTokenKind.VariationEnd);
            }
            else if (ch == '$' && PeekIsDigit(1))
            {
                ReadNag();
            }
            else if (TryReadResult())
            {
            }
            else if (char.IsDigit(ch) && TryReadMoveNumber())
            {
            }
            else
            {
                ReadSymbol();
            }
        }

        return (_tokens.ToArray(), _diagnostics.ToArray());
    }

    private void ReadWhile(PgnTokenKind kind, Func<char, bool> predicate)
    {
        var start = _index;
        var line = _line;
        var column = _column;
        while (_index < _source.Length && predicate(_source[_index]))
        {
            Advance(_source[_index]);
        }

        _tokens.Add(new PgnToken(kind, _source[start.._index], start, line, column));
    }

    private void ReadHeader()
    {
        var start = _index;
        var line = _line;
        var column = _column;
        var quoted = false;
        var escaped = false;
        while (_index < _source.Length)
        {
            var ch = _source[_index];
            Advance(ch);
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ']')
            {
                _tokens.Add(new PgnToken(PgnTokenKind.Header, _source[start.._index], start, line, column));
                return;
            }
        }

        _tokens.Add(new PgnToken(PgnTokenKind.Header, _source[start.._index], start, line, column));
        _diagnostics.Add(new PgnDiagnostic("PGN001", "Unclosed PGN header.", start, line, column));
    }

    private void ReadBraceComment()
    {
        var start = _index;
        var line = _line;
        var column = _column;
        while (_index < _source.Length)
        {
            var ch = _source[_index];
            Advance(ch);
            if (ch == '}')
            {
                _tokens.Add(new PgnToken(PgnTokenKind.BraceComment, _source[start.._index], start, line, column));
                return;
            }
        }

        _tokens.Add(new PgnToken(PgnTokenKind.BraceComment, _source[start.._index], start, line, column));
        _diagnostics.Add(new PgnDiagnostic("PGN002", "Unclosed PGN brace comment.", start, line, column));
    }

    private void ReadLineComment()
    {
        var start = _index;
        var line = _line;
        var column = _column;
        while (_index < _source.Length && _source[_index] is not ('\r' or '\n'))
        {
            Advance(_source[_index]);
        }

        _tokens.Add(new PgnToken(PgnTokenKind.LineComment, _source[start.._index], start, line, column));
    }

    private void ReadNag()
    {
        var start = _index;
        var line = _line;
        var column = _column;
        Advance(_source[_index]);
        while (_index < _source.Length && char.IsDigit(_source[_index]))
        {
            Advance(_source[_index]);
        }

        _tokens.Add(new PgnToken(PgnTokenKind.Nag, _source[start.._index], start, line, column));
    }

    private bool TryReadResult()
    {
        foreach (var result in Results)
        {
            if (!_source.AsSpan(_index).StartsWith(result, StringComparison.Ordinal))
            {
                continue;
            }

            var end = _index + result.Length;
            if (end < _source.Length && !IsDelimiter(_source[end]))
            {
                continue;
            }

            AddRaw(PgnTokenKind.Result, result);
            return true;
        }

        return false;
    }

    private bool TryReadMoveNumber()
    {
        var cursor = _index;
        while (cursor < _source.Length && char.IsDigit(_source[cursor]))
        {
            cursor++;
        }

        if (cursor >= _source.Length || _source[cursor] != '.')
        {
            return false;
        }

        while (cursor < _source.Length && _source[cursor] == '.')
        {
            cursor++;
        }

        var raw = _source[_index..cursor];
        AddRaw(PgnTokenKind.MoveNumber, raw);
        return true;
    }

    private void ReadSymbol()
    {
        var start = _index;
        var line = _line;
        var column = _column;
        while (_index < _source.Length && !IsDelimiter(_source[_index]))
        {
            Advance(_source[_index]);
        }

        if (start == _index)
        {
            Advance(_source[_index]);
        }

        var raw = _source[start.._index];
        var kind = raw is "!" or "?" or "!!" or "??" or "!?" or "?!"
            ? PgnTokenKind.Annotation
            : PgnTokenKind.Symbol;
        _tokens.Add(new PgnToken(kind, raw, start, line, column));
    }

    private void AddSingle(PgnTokenKind kind) => AddRaw(kind, _source[_index].ToString());

    private void AddRaw(PgnTokenKind kind, string raw)
    {
        var start = _index;
        var line = _line;
        var column = _column;
        foreach (var ch in raw)
        {
            Advance(ch);
        }

        _tokens.Add(new PgnToken(kind, raw, start, line, column));
    }

    private bool PeekIsDigit(int delta) =>
        _index + delta < _source.Length && char.IsDigit(_source[_index + delta]);

    private static bool IsDelimiter(char value) =>
        char.IsWhiteSpace(value) || value is '[' or ']' or '{' or '}' or '(' or ')' or ';' or '$';

    private void Advance(char value)
    {
        _index++;
        if (value == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
    }
}
