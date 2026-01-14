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
        string? locatieVanHerkomst = null;
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

                // 1. Extract Record ID (starts with DE)
                if (recordId == null)
                {
                    var deMatch = RecordIdRegex().Match(line.Text);
                    if (deMatch.Success)
                    {
                        recordId = deMatch.Value;
                    }
                }

                // 2. Extract Locatie van herkomst (first line of address after the label)
                if (locatieVanHerkomst == null && lowerText.Contains("locatie") && lowerText.Contains("herkomst"))
                {
                    // Check the next line for the street address
                    if (i + 1 < allLines.Count)
                    {
                        var nextLine = allLines[i + 1].Text.Trim();
                        // Make sure it looks like an address (contains street name, not just a label)
                        if (IsLikelyStreetAddress(nextLine))
                        {
                            locatieVanHerkomst = nextLine;
                        }
                    }
                }

                // 3. Extract Weight - look for patterns with "kg" or standalone numbers that could be weight
                if (weight == null)
                {
                    // Look for weight in the "gewogen hoeveelheid" column or similar
                    if (lowerText.Contains("gewogen") || lowerText.Contains("hoeveelheid"))
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

                    // Also look for "kg" patterns anywhere
                    var kgMatch = KgPatternRegex().Match(line.Text);
                    if (kgMatch.Success)
                    {
                        weight = kgMatch.Groups[1].Value;
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
        }

        return new OcrResult
        {
            AllExtractedText = allText,
            RecordId = recordId,
            LocatieVanHerkomst = locatieVanHerkomst,
            Weight = weight
        };
    }

    private static bool IsLikelyStreetAddress(string text)
    {
        // Check if text looks like a street address
        // Usually contains a street name with a number, or common Dutch street suffixes
        var lower = text.ToLowerInvariant();

        // Common Dutch street patterns
        if (lower.Contains("straat") || lower.Contains("weg") || lower.Contains("laan") ||
            lower.Contains("plein") || lower.Contains("singel") || lower.Contains("kade") ||
            lower.Contains("gracht") || lower.Contains("steeg") || lower.Contains("hof"))
        {
            return true;
        }

        // Check if it has a number (typical for addresses)
        return Regex.IsMatch(text, @"\d+");
    }

    // Regex for Record ID starting with DE followed by digits
    [GeneratedRegex(@"DE\d{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex RecordIdRegex();

    // Regex for weight numbers
    [GeneratedRegex(@"(\d+[,.]?\d*)\s*$")]
    private static partial Regex WeightNumberRegex();

    // Regex for kg pattern
    [GeneratedRegex(@"(\d+[,.]?\d*)\s*kg", RegexOptions.IgnoreCase)]
    private static partial Regex KgPatternRegex();

    // Regex for standalone weight (large numbers, possibly handwritten totals)
    [GeneratedRegex(@"^(\d{3,5})$")]
    private static partial Regex StandaloneWeightRegex();
}

public class OcrResult
{
    public List<string> AllExtractedText { get; set; } = [];
    public string? RecordId { get; set; }
    public string? LocatieVanHerkomst { get; set; }
    public string? Weight { get; set; }
}
