using System.Text.RegularExpressions;
using Azure;
using Azure.AI.Vision.ImageAnalysis;

namespace OCR_poc.Services;

public partial class OcrService
{
    private readonly ImageAnalysisClient _client;

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

                // 2. Extract Supplier Name (location name from "locatie van herkomst" section)
                if (supplierName == null && lowerText.Contains("locatie") && lowerText.Contains("herkomst"))
                {
                    // Look at subsequent lines for the supplier/location name
                    // Skip street addresses (end with numbers) and postal codes (start with 4 digits)
                    for (int j = i + 1; j < Math.Min(i + 4, allLines.Count); j++)
                    {
                        var candidateLine = allLines[j].Text.Trim();
                        if (IsLikelySupplierName(candidateLine))
                        {
                            supplierName = candidateLine;
                            break;
                        }
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

    private static bool IsLikelySupplierName(string text)
    {
        var trimmed = text.Trim();

        // Skip empty or very short text
        if (trimmed.Length < 3)
            return false;

        // Skip if it's just numbers
        if (Regex.IsMatch(trimmed, @"^\d+$"))
            return false;

        // Skip if it's a street address with house number at end (e.g., "Marinierstraat 4")
        if (Regex.IsMatch(trimmed, @"\d+\s*$"))
            return false;

        // Skip if it's a Dutch postal code line (4 digits + 2 letters + optional city)
        if (Regex.IsMatch(trimmed, @"^\d{4}\s*[A-Za-z]{2}"))
            return false;

        // Skip common label text
        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("straat + nr") || lower.Contains("postc") || lower.Contains("woonpl"))
            return false;

        // Accept location/supplier names (typically text without trailing numbers)
        return true;
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
