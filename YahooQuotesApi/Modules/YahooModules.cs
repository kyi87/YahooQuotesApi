﻿using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YahooQuotesApi;

public sealed class YahooModules
{
    private readonly ILogger Logger;
    private readonly IHttpClientFactory HttpClientFactory;

    public YahooModules(ILogger logger, IHttpClientFactory factory)
    {
        Logger = logger;
        HttpClientFactory = factory;
    }

    internal async Task<Result<JsonProperty[]>> GetModulesAsync(string symbol, string[] modules, CancellationToken ct)
    {
        if (!Symbol.TryCreate(symbol, out var sym) || sym.IsCurrency)
            throw new ArgumentException($"Invalid symbol: {sym.Name}.");

        if (!modules.Any())
            throw new ArgumentException("No modules indicated.");
        if (modules.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Invalid module: \"\"");
        string[] dups = modules.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (dups.Any())
            return Result<JsonProperty[]>.Fail($"Duplicate module(s): \'{string.Join(", ", dups)}\'.");

        Result<JsonProperty[]> result = await Produce(symbol, modules, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<Result<JsonProperty[]>> Produce(string symbol, string[] modulesRequested, CancellationToken ct)
    {
        Uri uri = GetUri(symbol, modulesRequested);

        HttpClient httpClient = HttpClientFactory.CreateClient("modules");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        //Don't use GetFromJsonAsync() or GetStreamAsync() because it would throw an exception
        //and not allow reading a json error messages such as NotFound.
        using HttpResponseMessage response = await httpClient.GetAsync(uri, ct).ConfigureAwait(false);
        // await using?
        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return GetModules(modulesRequested, jsonDocument);
    }

    private Uri GetUri(string symbol, string[] modules)
    {
        const string address = "https://query2.finance.yahoo.com/v11/finance/quoteSummary";
        string url = $"{address}/{symbol}?modules={string.Join(",", modules)}";

        Logger.LogInformation("{Url}", url);

        return new Uri(url);
    }

    private static Result<JsonProperty[]> GetModules(string[] modulesRequested, JsonDocument jsonDocument)
    {
        if (!jsonDocument.RootElement.TryGetProperty("quoteSummary", out JsonElement quoteSummary))
            throw new InvalidDataException("quoteSummary");
        if (!quoteSummary.TryGetProperty("error", out JsonElement error))
            throw new InvalidDataException("error");
        if (error.ValueKind is not JsonValueKind.Null)
        {
            if (error.TryGetProperty("description", out JsonElement property))
            {
                string? description = property.GetString();
                if (description is not null)
                    return Result<JsonProperty[]>.Fail(description);
            }
            return Result<JsonProperty[]>.Fail(error.ToString());
        }

        if (!quoteSummary.TryGetProperty("result", out JsonElement result))
            throw new InvalidDataException("result");
        JsonElement[] items = result.EnumerateArray().ToArray();
        if (items.Length != 1)
            throw new InvalidDataException($"Error requesting YahooModules list.");
        JsonElement item = items.Single();
        JsonProperty[] modules = item.EnumerateObject().ToArray();

        return VerifiedModules(modulesRequested, modules);
    }

    private static Result<JsonProperty[]> VerifiedModules(string[] moduleNamesRequested, JsonProperty[] modules)
    {
        string[] moduleNames = modules.Select(module => module.Name).ToArray();

        string[] missingModules = moduleNamesRequested.Where(n => !moduleNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missingModules.Any())
            return Result<JsonProperty[]>.Fail($"Invalid module(s): \'{string.Join(", ", missingModules)}\'.");

        string[] extraModules = moduleNames.Where(n => !moduleNamesRequested.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (extraModules.Any())
            return Result<JsonProperty[]>.Fail($"Extra module(s): \'{string.Join(", ", extraModules)}\'.");

        if (moduleNamesRequested.Length != modules.Length)
            return Result<JsonProperty[]>.Fail($"Invalid modules.");

        return modules.ToResult();
    }
}
