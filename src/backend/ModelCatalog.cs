namespace Helpdesk.Backend;

public sealed record ModelOption(string Id, string Label);
public sealed record ModelCatalogResponse(string? DefaultModelId, IReadOnlyList<ModelOption> Models);
public sealed class ConfiguredModel
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string Api { get; set; } = "chatCompletions";
    public string? ReasoningEffort { get; set; }
    public ModelPricing? Pricing { get; set; }
}
public sealed class ModelPricing
{
    public decimal InputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public decimal? CacheWritePerMillion { get; set; }
    public long MaximumInputTokens { get; set; }
    public string Source { get; set; } = "";
    public string AsOf { get; set; } = "";
}

public sealed class ModelCatalog
{
    private readonly ConfiguredModel[] models;
    private readonly string defaultId;
    public ModelCatalog(IConfiguration config)
    {
        models = config.GetSection("AzureOpenAI:Models").Get<ConfiguredModel[]>() ?? [];
        if (models.Length == 0)
        {
            models = [new()
            {
                Id = "default", Label = "Configured model", Deployment = config["AzureOpenAI:Deployment"] ?? "",
                ReasoningEffort = config["AzureOpenAI:ReasoningEffort"]
            }];
        }
        else if (models.Length > 8 || models.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 80 ||
            m.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            string.IsNullOrWhiteSpace(m.Label) || m.Label.Length > 100 || string.IsNullOrWhiteSpace(m.Deployment) ||
            m.Deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) ||
            models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != models.Length)
            throw new InvalidOperationException("Azure OpenAI model catalog contains invalid or duplicate entries.");
        foreach (var model in models)
        {
            if (model.Api is not ("chatCompletions" or "responses"))
                throw new InvalidOperationException("A model has an unsupported Azure OpenAI API.");
            if (!string.IsNullOrEmpty(model.ReasoningEffort) && model.ReasoningEffort is not ("minimal" or "low"))
                throw new InvalidOperationException("A model has an unsupported reasoning effort.");
            if (model.Pricing is { } pricing && (pricing.InputPerMillion <= 0 || pricing.CachedInputPerMillion is null or < 0 ||
                pricing.OutputPerMillion <= 0 || pricing.CacheWritePerMillion < 0 || pricing.MaximumInputTokens <= 0 ||
                !Uri.TryCreate(pricing.Source, UriKind.Absolute, out var source) || source.Scheme != "https" ||
                !DateOnly.TryParseExact(pricing.AsOf, "yyyy-MM-dd", out _)))
                throw new InvalidOperationException("Model pricing must have verified rates, an applicable input limit, source and date.");
        }
        defaultId = config["AzureOpenAI:DefaultModelId"] ?? models[0].Id;
        if (!models.Any(m => m.Id == defaultId)) throw new InvalidOperationException("The default model is not in the configured catalog.");
    }
    public bool TryResolve(string? id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConfiguredModel? model)
    {
        model = models.SingleOrDefault(m => m.Id == (id ?? defaultId));
        return model is not null;
    }
    public ModelCatalogResponse PublicCatalog()
    {
        var available = models.Where(m => m.Deployment.Length > 0).Select(m => new ModelOption(m.Id, m.Label)).ToArray();
        return new(available.Length == 0 ? null : defaultId, available);
    }
}
