namespace VSoftSol.Syslog.Ingestion.Extraction;

/// <summary>
/// Working state for one message passing through the <see cref="ExtractorPipeline"/>.
/// Field additions are capped (<see cref="MaxFields"/>, <see cref="MaxValueLength"/>) so a
/// hostile message cannot exhaust memory (PHASE_03 Security Validation).
/// </summary>
public sealed class ExtractionContext
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    public ExtractionContext(string message, string sourceIp, string? hostname, string? appName, int maxFields, int maxValueLength)
    {
        Message = message ?? string.Empty;
        SourceIp = sourceIp;
        Hostname = hostname;
        AppName = appName;
        MaxFields = maxFields;
        MaxValueLength = maxValueLength;
    }

    public string Message { get; set; }

    public string SourceIp { get; }

    public string? Hostname { get; }

    public string? AppName { get; }

    public int MaxFields { get; }

    public int MaxValueLength { get; }

    /// <summary>True once the field cap was hit — further adds are silently dropped and
    /// this is surfaced as the <c>fields_truncated</c> field by the pipeline.</summary>
    public bool FieldCapHit { get; private set; }

    public IReadOnlyDictionary<string, string> Fields => _fields;

    /// <summary>Adds or replaces a field. Empty names/values are ignored; long values are
    /// clamped; adds past the cap are dropped and <see cref="FieldCapHit"/> is set.</summary>
    public bool Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Length > MaxValueLength)
        {
            value = value[..MaxValueLength];
        }

        if (!_fields.ContainsKey(name) && _fields.Count >= MaxFields)
        {
            FieldCapHit = true;
            return false;
        }

        _fields[name] = value;
        return true;
    }

    /// <summary>Sets a pipeline meta field (<c>fields_truncated</c>, <c>extractor_error</c>,
    /// …). Bypasses <see cref="MaxFields"/> so the cap-hit signal is never itself dropped.</summary>
    public void SetMeta(string name, string value)
    {
        if (value.Length > MaxValueLength)
        {
            value = value[..MaxValueLength];
        }

        _fields[name] = value;
    }

    public bool TryGet(string name, out string value) => _fields.TryGetValue(name, out value!);

    public bool Remove(string name) => _fields.Remove(name);

    public void Rename(string from, string to)
    {
        if (_fields.Remove(from, out string? value))
        {
            Set(to, value);
        }
    }
}
