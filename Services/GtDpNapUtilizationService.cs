using ClosedXML.Excel;
using ExcelDataReader;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Text;

namespace SlotAd_Globe.Services;

public sealed class GtDpNapUtilizationService : IGtDpNapUtilizationService
{
    private readonly string _reportsDirectory;

    public GtDpNapUtilizationService(IConfiguration configuration)
    {
        _reportsDirectory = configuration.GetValue<string>("ReportSessions:ReportsDirectory") ?? "App_Data/reports";
        Directory.CreateDirectory(_reportsDirectory);
    }

    public string GetZipFilePath(string batchId) =>
        Path.Combine(_reportsDirectory, $"GtDpNap_{batchId}.zip");

    public async Task<string> ProcessAndZipAsync(Stream xlsxStream, string originalFileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var groupedData = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var requiredCols = new[] { "DP", "DP/NAP LAT", "DP/NAP LONG", "S_SP", "S_Total", "CFS Area", "CFS Cluster", "DP Location" };

            using (var reader = ExcelReaderFactory.CreateReader(xlsxStream))
            {
                if (!reader.Read()) throw new InvalidOperationException("Worksheet is empty");

                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.GetValue(i)?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        headers[val] = i;
                    }
                }

                foreach (var req in requiredCols)
                {
                    if (!headers.ContainsKey(req))
                    {
                        throw new InvalidOperationException($"Missing required column: {req}");
                    }
                }

                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cfsArea = reader.GetValue(headers["CFS Area"])?.ToString()?.Trim() ?? "";
                    var sTotalRaw = reader.GetValue(headers["S_Total"])?.ToString()?.Trim() ?? "";

                    if (cfsArea.Equals("SOUTH MINDANAO 1", StringComparison.OrdinalIgnoreCase) && 
                        (sTotalRaw == "8" || sTotalRaw == "8.0" || sTotalRaw == "8.00"))
                    {
                        var cfsCluster = reader.GetValue(headers["CFS Cluster"])?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(cfsCluster))
                        {
                            cfsCluster = "UNKNOWN_CLUSTER";
                        }

                        if (!groupedData.ContainsKey(cfsCluster))
                        {
                            groupedData[cfsCluster] = new List<Dictionary<string, string>>();
                        }

                        var rowData = new Dictionary<string, string>();
                        foreach (var req in requiredCols)
                        {
                            rowData[req] = reader.GetValue(headers[req])?.ToString()?.Trim() ?? "";
                        }
                        groupedData[cfsCluster].Add(rowData);
                    }
                }
            }

            var batchId = Guid.NewGuid().ToString("N");
            var zipPath = GetZipFilePath(batchId);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var kvp in groupedData)
                {
                    var clusterName = string.Join("_", kvp.Key.Split(Path.GetInvalidFileNameChars()));
                    var entry = archive.CreateEntry($"{clusterName}.xlsx", CompressionLevel.Fastest);

                    using var entryStream = entry.Open();
                    using var outWb = new XLWorkbook();
                    var outWs = outWb.Worksheets.Add("Data");

                    // Write headers
                    for (int i = 0; i < requiredCols.Length; i++)
                    {
                        outWs.Cell(1, i + 1).Value = requiredCols[i];
                    }

                    // Write rows
                    int r = 2;
                    foreach (var rowData in kvp.Value)
                    {
                        for (int i = 0; i < requiredCols.Length; i++)
                        {
                            outWs.Cell(r, i + 1).Value = rowData[requiredCols[i]];
                        }
                        r++;
                    }

                    outWs.Columns().AdjustToContents();
                    outWb.SaveAs(entryStream);
                }
            }

            return batchId;
        }, cancellationToken);
    }
}
