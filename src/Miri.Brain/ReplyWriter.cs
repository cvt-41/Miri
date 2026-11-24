using System.Text;
using Miri.Core;

namespace Miri.Brain;

/// <summary>Forwards reply text to IMiriEvents; switches mood to Talking on the first token.</summary>
internal sealed class ReplyWriter
{
    private readonly IMiriEvents _events;
    private readonly StringBuilder _text = new();

    public ReplyWriter(IMiriEvents events) => _events = events;

    public bool Started { get; private set; }
    public string Text => _text.ToString();

    public void Token(string piece)
    {
        if (string.IsNullOrEmpty(piece)) return;
        if (!Started)
        {
            Started = true;
            _events.OnMood(PetMood.Talking);
        }
        _text.Append(piece);
        _events.OnToken(piece);
    }

    /// <summary>Emits a full sentence, separated from any earlier text.</summary>
    public void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (Started && _text.Length > 0 && !char.IsWhiteSpace(_text[^1])) Token("\n");
        Token(text);
    }
}
