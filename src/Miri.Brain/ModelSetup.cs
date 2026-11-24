using Microsoft.Win32;
using Miri.Core;

namespace Miri.Brain;

/// <summary>A chat model (answers questions, drives apps) plus a vision model (sees the screen).</summary>
public sealed record ModelPair(string Label, string Chat, string Vision, double DownloadGb, double MinRamGb, string Note);

public sealed record PcSpecs(double RamGb, double VramGb, string Gpu)
{
    public static PcSpecs Detect()
    {
        double ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
        double vram = 0; string gpu = "";
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is not long bytes) continue;
                if (bytes / 1073741824.0 > vram) { vram = bytes / 1073741824.0; gpu = k.GetValue("DriverDesc") as string ?? ""; }
            }
        }
        catch (Exception) { }   // no access: treat as integrated graphics
        return new PcSpecs(Math.Round(ram, 1), Math.Round(vram, 1), gpu);
    }
}

public static class ModelAdvisor
{
    // shortcut: sizes are Ollama's q4 downloads as of 2026-11; refresh when models change.
    public static readonly ModelPair[] Pairs =
    {
        new("Light",    "qwen3:1.7b", "qwen2.5vl:3b",  4.6,  4, "for 4–8 GB RAM: quick, simpler answers"),
        new("Balanced", "qwen3.5:4b", "gemma4:e4b",    9.0,  8, "Miri's default: good answers on 8–16 GB RAM"),
        new("Strong",   "qwen3:8b",   "qwen2.5vl:7b", 11.2, 16, "for a GPU with 8 GB+ (or lots of patience on CPU): smarter"),
        new("Power",    "qwen3:14b",  "gemma3:12b",   17.4, 32, "for a GPU with 16 GB+: smartest, slow without one"),
    };

    /// <summary>Index of the best pair this PC runs comfortably. A big GPU counts as extra RAM.</summary>
    public static int Recommend(PcSpecs pc)
    {
        double budget = Math.Max(pc.RamGb, pc.VramGb >= 6 ? pc.VramGb * 2 : 0);
        int best = 0;
        for (int i = 0; i < Pairs.Length; i++) if (budget >= Pairs[i].MinRamGb - 1.0) best = i;   // Windows reports ~7.4 GB on an "8 GB" PC
        return pc.VramGb >= 6 ? best : Math.Min(best, 1);   // CPU only: bigger than 4B fits but answers too slowly
    }

    /// <summary>Downloads the pair, switches Miri to it, then deletes the models it replaces. Old models stay if a download fails.</summary>
    public static async Task SwitchAsync(MiriSettings settings, ModelPair pair, IProgress<string> status, CancellationToken ct)
    {
        using var client = new OllamaClient(settings.OllamaUrl);
        var old = new[] { settings.ChatModel, settings.AlternateModel, settings.VisionModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in new[] { pair.Chat, pair.Vision }.Distinct())
            await client.PullAsync(m, new Progress<double>(p => status.Report($"Downloading {m}… {p:P0}")), ct).ConfigureAwait(false);

        settings.ChatModel = pair.Chat;
        settings.AlternateModel = pair.Vision;
        settings.VisionModel = pair.Vision;
        settings.ModelSetupDone = true;
        settings.Save();

        foreach (var m in old.Where(m => !m.Equals(pair.Chat, StringComparison.OrdinalIgnoreCase) && !m.Equals(pair.Vision, StringComparison.OrdinalIgnoreCase)))
        {
            status.Report($"Removing {m}…");
            try { await client.DeleteAsync(m, ct).ConfigureAwait(false); } catch (OllamaException) { }   // already gone: fine
        }
        status.Report("Done");
    }

    /// <summary>Self-check for <see cref="Recommend"/>; throws on regression.</summary>
    public static void SelfCheck()
    {
        void Is(double ram, double vram, string want)
        {
            var got = Pairs[Recommend(new PcSpecs(ram, vram, ""))].Label;
            if (got != want) throw new Exception($"ram {ram} vram {vram}: {got}, want {want}");
        }
        Is(3.8, 0, "Light"); Is(6.5, 0.5, "Light"); Is(7.4, 0.5, "Balanced"); Is(15.4, 0.5, "Balanced"); Is(64, 0, "Balanced");
        Is(14.5, 0.5, "Balanced"); Is(16, 8, "Strong"); Is(16, 12, "Strong"); Is(16, 16, "Power"); Is(32, 6, "Power");
    }
}
