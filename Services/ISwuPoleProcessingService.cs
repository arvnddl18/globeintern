using SlotAd_Globe.Models;

namespace SlotAd_Globe.Services;

public interface ISwuPoleProcessingService
{
    Task<SwuReorganizedSummary> ReorganizeAndAppendToBatchAsync(
        Stream xlsxStream,
        string originalFileName,
        string batchId,
        bool isFirstInBatch,
        CancellationToken cancellationToken = default);

    string GetBatchFilePath(string batchId);

    void ClearBatch(string batchId);

    bool BatchFileExists(string batchId);
}
