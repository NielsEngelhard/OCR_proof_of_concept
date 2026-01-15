using System.Text.RegularExpressions;
using Azure;
using Azure.AI.Vision.ImageAnalysis;

namespace OCR_poc.Services;

public partial class OcrService
{
    private readonly ImageAnalysisClient _client;

    // Known supplier names for matching
    private static readonly string[] KnownSuppliers =
    [
        "2Switch Arnhem",
        "Aanbiedstation Micro-Zevenaar",
        "Aanbiedstation 's-Heerenberg",
        "ABS Doornenburg - VOS Transport VOF",
        "ABS Huissen - Heijting Milieuservice",
        "ABS Huissen - Van Dalen BV",
        "Afvalbrengstation Arnhem Noord",
        "Afvalbrengstation Arnhem Zuid",
        "De Coolhof",
        "Gemeentewerf Beuningen",
        "Gemeentewerf De Moestuin",
        "Gemeentewerf Dieren",
        "Gemeentewerf Duiven",
        "Gemeentewerf Millingen aan de Rijn",
        "Gemeentewerf Rijnwaarden",
        "Heijting Milieuservice Oosterhout BV",
        "Kringloop 2Switch Dieren",
        "Kringloop Het Goed Nijmegen",
        "Kringloopwinkel 2Switch Elst",
        "Kringloopwinkel 2Switch Tiel",
        "Kringloopwinkel 2Switch Westervoort",
        "Milieustation Gennep",
        "Milieustraat Albion",
        "Milieustraat Andelst",
        "Milieustraat Bijsterhuizen",
        "Milieustraat Boekel",
        "Milieustraat Druten",
        "Milieustraat Elst",
        "Milieustraat Groesbeek",
        "Milieustraat Haps",
        "Milieustraat Malden",
        "Milieustraat Mook en Middelaar",
        "Milieustraat Nijmegen",
        "Milieustraat Oss",
        "Milieustraat Rosmalen",
        "Milieustraat Treurenburg",
        "Milieustraat Uden",
        "Milieustraat Waalwijk",
        "Stichting Actief",
        "Stichting Actief Cuijk",
        "Stichting Actief - Gem. Bergen",
        "Stichting Aktief Groep"
    ];

    public OcrService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureComputerVision:Endpoint"]
            ?? throw new InvalidOperationException("AzureComputerVision:Endpoint is not configured");
        var key = configuration["AzureComputerVision:Key"]
            ?? throw new InvalidOperationException("AzureComputerVision:Key is not configured");

        _client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<OcrResult> ExtractTextFromImageAsync(Stream imageStream)
    {
        var binaryData = await BinaryData.FromStreamAsync(imageStream);

        var result = await _client.AnalyzeAsync(
            binaryData,
            VisualFeatures.Read);

        var allText = new List<string>();
        string? recordId = null;
        string? supplierName = null;
        string? weight = null;

        if (result.Value.Read?.Blocks != null)
        {
            var allLines = result.Value.Read.Blocks
                .SelectMany(b => b.Lines)
                .ToList();

            for (int i = 0; i < allLines.Count; i++)
            {
                var line = allLines[i];
                allText.Add(line.Text);
                var lowerText = line.Text.ToLowerInvariant();

                // 1. Extract Record ID - prioritize DE, AR, TL prefixes
                if (recordId == null)
                {
                    // First priority: look for DE, AR, or TL prefixed IDs
                    var preferredMatch = PreferredRecordIdRegex().Match(line.Text);
                    if (preferredMatch.Success)
                    {
                        recordId = preferredMatch.Value;
                    }
                }

                // 2. Extract Supplier Name by matching against known suppliers list
                if (supplierName == null)
                {
                    var matchedSupplier = FindMatchingSupplier(line.Text);
                    if (matchedSupplier != null)
                    {
                        supplierName = matchedSupplier;
                    }
                }

                // 3. Extract Weight - prioritize "netto" (net weight), then other patterns
                if (weight == null)
                {
                    // Priority 1: Look for "netto" (net weight - most accurate value)
                    if (lowerText.Contains("netto"))
                    {
                        var nettoMatch = NettoWeightRegex().Match(line.Text);
                        if (nettoMatch.Success)
                        {
                            weight = nettoMatch.Groups[1].Value;
                        }
                    }

                    // Priority 2: Look for weight in the "gewogen hoeveelheid" column or similar
                    if (weight == null && (lowerText.Contains("gewogen") || lowerText.Contains("hoeveelheid")))
                    {
                        var weightMatch = WeightNumberRegex().Match(line.Text);
                        if (weightMatch.Success)
                        {
                            var value = weightMatch.Groups[1].Value.Replace(",", ".");
                            if (double.TryParse(value, out var w) && w > 0)
                            {
                                weight = weightMatch.Groups[1].Value;
                            }
                        }
                    }

                    // Priority 3: Look for "kg" patterns anywhere
                    if (weight == null)
                    {
                        var kgMatch = KgPatternRegex().Match(line.Text);
                        if (kgMatch.Success)
                        {
                            weight = kgMatch.Groups[1].Value;
                        }
                    }
                }
            }

            // Second pass: look for handwritten weight calculations at the bottom
            // These often appear as standalone numbers (like "2120")
            if (weight == null || weight == "0")
            {
                foreach (var line in allLines)
                {
                    // Look for standalone large numbers that could be weight totals
                    var standaloneMatch = StandaloneWeightRegex().Match(line.Text.Trim());
                    if (standaloneMatch.Success)
                    {
                        var value = standaloneMatch.Groups[1].Value;
                        if (int.TryParse(value, out var w) && w >= 100 && w <= 100000)
                        {
                            weight = value;
                        }
                    }
                }
            }

            // Fallback: if no preferred record ID (DE, AR, TL) was found, look for any two-letter prefix
            if (recordId == null)
            {
                foreach (var line in allLines)
                {
                    var fallbackMatch = FallbackRecordIdRegex().Match(line.Text);
                    if (fallbackMatch.Success)
                    {
                        recordId = fallbackMatch.Value;
                        break;
                    }
                }
            }
        }

        return new OcrResult
        {
            AllExtractedText = allText,
            RecordId = recordId,
            SupplierName = supplierName,
            Weight = weight
        };
    }

    private static string? FindMatchingSupplier(string text)
    {
        var lowerText = text.ToLowerInvariant();

        // Priority 1: Exact/full supplier name match
        foreach (var supplier in KnownSuppliers)
        {
            if (lowerText.Contains(supplier.ToLowerInvariant()))
            {
                return supplier;
            }
        }

        // Priority 2: Match on key location identifiers (city names, specific words)
        // Extract the distinguishing part of each supplier (usually the last word/city name)
        foreach (var supplier in KnownSuppliers)
        {
            var keywords = GetSupplierKeywords(supplier);
            foreach (var keyword in keywords)
            {
                // Check if the keyword appears as a whole word in the text
                if (Regex.IsMatch(lowerText, $@"\b{Regex.Escape(keyword.ToLowerInvariant())}\b"))
                {
                    return supplier;
                }
            }
        }

        return null;
    }

    private static List<string> GetSupplierKeywords(string supplier)
    {
        // Extract meaningful keywords from supplier names for flexible matching
        // Focus on city names and unique identifiers
        var keywords = new List<string>();

        // Known city/location names that appear in supplier names
        string[] locationKeywords =
        [
            "Arnhem", "Zevenaar", "'s-Heerenberg", "Heerenberg", "Doornenburg", "Huissen",
            "Beuningen", "Dieren", "Duiven", "Millingen", "Rijnwaarden", "Oosterhout",
            "Nijmegen", "Elst", "Tiel", "Westervoort", "Gennep", "Albion", "Andelst",
            "Bijsterhuizen", "Boekel", "Druten", "Groesbeek", "Haps", "Malden", "Mook",
            "Middelaar", "Oss", "Rosmalen", "Treurenburg", "Uden", "Waalwijk", "Cuijk",
            "Bergen", "Coolhof", "Moestuin", "Maashorst"
        ];

        foreach (var location in locationKeywords)
        {
            if (supplier.Contains(location, StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(location);
            }
        }

        // Also add unique identifiers
        if (supplier.Contains("2Switch")) keywords.Add("2Switch");
        if (supplier.Contains("Het Goed")) keywords.Add("Het Goed");
        if (supplier.Contains("Actief") || supplier.Contains("Aktief"))
        {
            keywords.Add("Actief");
            keywords.Add("Aktief");
        }
        if (supplier.Contains("Heijting")) keywords.Add("Heijting");
        if (supplier.Contains("VOS Transport")) keywords.Add("VOS");
        if (supplier.Contains("Van Dalen")) keywords.Add("Van Dalen");

        return keywords;
    }

    // Regex for preferred Record ID prefixes: DE, AR, or TL followed by digits (not followed by more letters)
    [GeneratedRegex(@"\b(DE|AR|TL)\d{6,}(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PreferredRecordIdRegex();

    // Fallback regex for Record ID: any two letters followed by only digits (not followed by more letters)
    [GeneratedRegex(@"\b[A-Za-z]{2}\d{6,}(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackRecordIdRegex();

    // Regex for weight numbers
    [GeneratedRegex(@"(\d+[,.]?\d*)\s*$")]
    private static partial Regex WeightNumberRegex();

    // Regex for kg pattern
    [GeneratedRegex(@"(\d+[,.]?\d*)\s*kg", RegexOptions.IgnoreCase)]
    private static partial Regex KgPatternRegex();

    // Regex for standalone weight (large numbers, possibly handwritten totals)
    [GeneratedRegex(@"^(\d{3,5})$")]
    private static partial Regex StandaloneWeightRegex();

    // Regex for "Netto" followed by number (e.g., "Netto 2130" or "Netto: 2130")
    [GeneratedRegex(@"netto[:\s]*(\d+[,.]?\d*)", RegexOptions.IgnoreCase)]
    private static partial Regex NettoWeightRegex();
}

public class OcrResult
{
    public List<string> AllExtractedText { get; set; } = [];
    public string? RecordId { get; set; }
    public string? SupplierName { get; set; }
    public string? Weight { get; set; }
}
