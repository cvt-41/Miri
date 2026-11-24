namespace Miri.Core;

// ---- brain-facing (implemented by Miri.Brain) ----
public enum Intent { Question, ScreenTask, ScreenQuestion, Chitchat }
public enum PetMood { Idle, Thinking, Talking, Happy, Confused, Pointing }

public interface IMiriEvents               // implemented by Miri.App; Brain calls it
{
    void OnToken(string text);              // streaming reply text
    void OnMood(PetMood mood);
    void OnPointAt(System.Drawing.Rectangle screenBounds);   // kitten walks/points there
    void OnStatus(string text);             // short status ("Looking at Telegram...")
    /// <summary>Like <see cref="OnPointAt"/>, but completes once the kitten is at the target (the executor waits before clicking).</summary>
    Task PointAtAsync(System.Drawing.Rectangle screenBounds) { OnPointAt(screenBounds); return Task.Delay(650); }
}

public interface IMiriBrain
{
    Task HandleAsync(string userText, CancellationToken ct);  // routes + answers/acts, streaming via IMiriEvents
    Task<IReadOnlyList<string>> AvailableModelsAsync();
    string ActiveModel { get; set; }
}
