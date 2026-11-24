using System.Text;

namespace Miri.Brain;

internal sealed record KnowledgeChunk(string Source, string Title, string Text);

/// <summary>Search abstraction so an embedding search (e.g. bge-m3) can replace keyword search later.</summary>
internal interface IKnowledgeSearch
{
    IReadOnlyList<KnowledgeChunk> Search(string query, int topK = 3);
}

/// <summary>
/// Loads knowledge/**/*.md, splits on "##" headings and ranks chunks with BM25 over
/// diacritic-stripped unigrams + bigrams (bigrams matter for Vietnamese: "ca phe", "sua da").
/// </summary>
internal sealed class KnowledgeBase : IKnowledgeSearch
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double MinScore = 1.5;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "a", "an", "the", "is", "are", "was", "be", "to", "of", "and", "or", "in", "on", "for", "with", "it",
        "this", "that", "i", "me", "my", "you", "your", "we", "do", "does", "how", "what", "which", "why", "can",
        "should", "would", "could", "about", "from", "at", "as", "by", "if", "so", "much", "many", "some", "any",
        "please", "tell", "miri", "get", "make", "want", "need", "best", "good", "there", "have", "has",
        // Vietnamese (diacritics stripped)
        "la", "cua", "va", "co", "khong", "cho", "toi", "ban", "minh", "nhu", "the", "nao", "gi", "de", "mot",
        "nhung", "cac", "duoc", "voi", "trong", "thi", "nay", "do", "em", "anh", "chi", "oi", "nhe", "a", "vay",
        "sao", "lam", "hay", "giup", "muon", "nen", "phai", "bao", "nhieu", "ve", "o", "khi", "neu", "thich",
    };

    private readonly List<KnowledgeChunk> _chunks = new();
    private readonly List<Dictionary<string, int>> _tf = new();
    private readonly List<int> _len = new();
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private double _avgLen;

    public int Count => _chunks.Count;

    public KnowledgeBase(string knowledgeDir)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(knowledgeDir) && Directory.Exists(knowledgeDir))
            {
                foreach (var file in Directory.EnumerateFiles(knowledgeDir, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
                    LoadFile(knowledgeDir, file);
            }
        }
        catch (Exception)
        {
            // Knowledge is optional; a broken folder must not break Miri.
        }
        BuildIndex();
    }

    private void LoadFile(string root, string file)
    {
        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); }
        catch (Exception) { return; }

        var source = Path.GetRelativePath(root, file).Replace('\\', '/');
        string docTitle = Path.GetFileNameWithoutExtension(file);
        string? heading = null;
        var body = new StringBuilder();

        void Flush()
        {
            var content = body.ToString().Trim();
            if (content.Length > 0)
            {
                var title = heading == null ? docTitle : $"{docTitle} — {heading}";
                _chunks.Add(new KnowledgeChunk(source, title, content));
            }
            body.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal) && heading == null && body.Length == 0)
            {
                docTitle = line[2..].Trim();
            }
            else
            {
                body.AppendLine(line);
            }
        }
        Flush();
    }

    private static List<string> Terms(string text)
    {
        var tokens = TextUtil.Tokenize(text);
        var terms = new List<string>(tokens.Count * 2);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!StopWords.Contains(t) && (t.Length > 1 || char.IsDigit(t[0]))) terms.Add(t);
            if (i + 1 < tokens.Count) terms.Add(t + "_" + tokens[i + 1]);   // bigram (stopwords allowed: "ca_phe", "cho_meo")
        }
        return terms;
    }

    private void BuildIndex()
    {
        foreach (var c in _chunks)
        {
            // Title words count double.
            var terms = Terms(c.Title + " " + c.Title + " " + c.Text);
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in terms) tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;
            _tf.Add(tf);
            _len.Add(terms.Count);
            foreach (var t in tf.Keys) _df[t] = _df.TryGetValue(t, out var d) ? d + 1 : 1;
        }
        _avgLen = _len.Count == 0 ? 1 : _len.Average();
    }

    public IReadOnlyList<KnowledgeChunk> Search(string query, int topK = 3)
    {
        if (_chunks.Count == 0 || string.IsNullOrWhiteSpace(query)) return Array.Empty<KnowledgeChunk>();
        var qTerms = Terms(query).Distinct().ToList();
        if (qTerms.Count == 0) return Array.Empty<KnowledgeChunk>();

        int n = _chunks.Count;
        var scored = new List<(int Index, double Score)>();
        for (int i = 0; i < n; i++)
        {
            double score = 0;
            var tf = _tf[i];
            foreach (var q in qTerms)
            {
                if (!tf.TryGetValue(q, out var f)) continue;
                var df = _df[q];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                var weight = q.Contains('_') ? 1.5 : 1.0;   // phrase matches are stronger evidence
                score += weight * idf * (f * (K1 + 1)) / (f + K1 * (1 - B + B * _len[i] / _avgLen));
            }
            if (score >= MinScore) scored.Add((i, score));
        }

        return scored.OrderByDescending(s => s.Score).Take(topK).Select(s => _chunks[s.Index]).ToList();
    }
}
