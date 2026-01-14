using Azure;
using Azure.AI.Vision.ImageAnalysis;

namespace OCR_poc.Services;

public class OcrService
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
        string? locatieVanHerkomst = null;

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

                // Check if this line contains the label "locatie van herkomst"
                if (lowerText.Contains("locatie") && lowerText.Contains("herkomst"))
                {
                    // Try to extract value from same line (after colon or label)
                    locatieVanHerkomst = ExtractValueAfterLabel(line.Text, "herkomst");

                    // If not found on same line, check the next line
                    if (string.IsNullOrWhiteSpace(locatieVanHerkomst) && i + 1 < allLines.Count)
                    {
                        locatieVanHerkomst = allLines[i + 1].Text.Trim();
                    }
                }
            }
        }

        return new OcrResult
        {
            AllExtractedText = allText,
            LocatieVanHerkomst = locatieVanHerkomst
        };
    }

    private static string? ExtractValueAfterLabel(string text, string labelEnd)
    {
        var lowerText = text.ToLowerInvariant();
        var labelIndex = lowerText.IndexOf(labelEnd, StringComparison.OrdinalIgnoreCase);

        if (labelIndex >= 0)
        {
            // Get text after the label
            var afterLabel = text[(labelIndex + labelEnd.Length)..].Trim();

            // Remove leading colon or other separators
            afterLabel = afterLabel.TrimStart(':', ' ', '\t');

            if (!string.IsNullOrWhiteSpace(afterLabel))
            {
                return afterLabel;
            }
        }

        return null;
    }
}

public class OcrResult
{
    public List<string> AllExtractedText { get; set; } = [];
    public string? LocatieVanHerkomst { get; set; }
}
