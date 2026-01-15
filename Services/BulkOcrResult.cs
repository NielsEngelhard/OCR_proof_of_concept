namespace OCR_poc.Services;

public class BulkOcrItem
{
    public required string FileName { get; set; }
    public required OcrResult Result { get; set; }
    public string? ImageDataUrl { get; set; }
    public bool RecordIdCorrect { get; set; }
    public bool LocationCorrect { get; set; }
    public bool WeightCorrect { get; set; }
}
