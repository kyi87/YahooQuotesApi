﻿using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Invalid symbols are ignored by Yahoo.

namespace YahooQuotesApi
{
    internal class YahooSnapshot
    {
        private readonly ILogger Logger;
        private readonly HttpClient HttpClient;
        private readonly SerialProducerCache<Symbol, Security?> Cache;
        private readonly bool UseHttpV2;

        internal YahooSnapshot(IClock clock, ILogger logger, IHttpClientFactory factory, Duration cacheDuration, bool useHttpV2)
        {
            Logger = logger;
            HttpClient = factory.CreateClient("snapshot");
            Cache = new SerialProducerCache<Symbol, Security?>(clock, cacheDuration, Producer);
            UseHttpV2 = useHttpV2;
        }

        internal async Task<Dictionary<Symbol, Security?>> GetAsync(HashSet<Symbol> symbols, CancellationToken ct = default)
        {
            Symbol? currency = symbols.FirstOrDefault(s => s.IsCurrency);
            if (currency is not null)
                throw new ArgumentException($"Invalid symbol: {currency} (currency).");

            return await Cache.Get(symbols, ct).ConfigureAwait(false);
        }
        private async Task<Dictionary<Symbol, Security?>> Producer(HashSet<Symbol> symbols, CancellationToken ct)
        {
			Dictionary<Symbol, Security?> dict = symbols.ToDictionary(s => s, s => (Security?)null);
            if (!symbols.Any())
                return dict;
            IEnumerable<JsonElement> elements = await GetElements(symbols, ct).ConfigureAwait(false);
            foreach (JsonElement element in elements)
            {
                Security security = new(element, Logger);
                Symbol symbol = security.Symbol;
                if (!dict.ContainsKey(symbol))
                    throw new InvalidOperationException(symbol.Name);
                dict[symbol] = security;
            }
            return dict;
        }

        private async Task<IEnumerable<JsonElement>> GetElements(IEnumerable<Symbol> symbols, CancellationToken ct)
        {
            List<(Uri uri, List<JsonElement> elements)> datas =
                GetUris(symbols)
                    .Select(uri => (uri, elements: new List<JsonElement>()))
                    .ToList();

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = 16,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(datas, parallelOptions, async (data, ct) =>
                data.elements.AddRange(await MakeRequest(data.uri, ct).ConfigureAwait(false)));

            return datas.Select(x => x.elements).SelectMany(x => x);
        }

        private static IEnumerable<Uri> GetUris(IEnumerable<Symbol> symbols)
        {
            const string baseUrl = "https://query2.finance.yahoo.com/v7/finance/quote?symbols=";

            return symbols
                .Select(symbol => WebUtility.UrlEncode(symbol.Name))
                .Chunk(100)
                .Select(s => $"{baseUrl}{string.Join(",", s)}")
                .Select(s => new Uri(s));
        }

        private async Task<List<JsonElement>> MakeRequest(Uri uri, CancellationToken ct)
        {
            Logger.LogInformation(uri.ToString());

            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            if (UseHttpV2)
                request.Version = new Version(2, 0);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            if (!jsonDocument.RootElement.TryGetProperty("quoteResponse", out JsonElement quoteResponse))
                throw new InvalidDataException("quoteResponse");
            if (!quoteResponse.TryGetProperty("error", out JsonElement error))
                throw new InvalidDataException("error");
            string? errorMessage = error.GetString();
            if (errorMessage != null)
                throw new InvalidDataException($"Error requesting YahooSnapshot: {errorMessage}.");
            if (!quoteResponse.TryGetProperty("result", out JsonElement result))
                throw new InvalidDataException("result");
            return result.EnumerateArray().ToList();
        }
    }
}
