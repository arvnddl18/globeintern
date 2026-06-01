namespace SlotAd_Globe.Services;

public interface IGtDpNapUtilizationService
{
    Task<string> ProcessAndZipAsync(Stream xlsxStream, string originalFileName, CancellationToken cancellationToken = default);
    string GetZipFilePath(string batchId);
}
