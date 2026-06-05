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

    private static readonly (double Lat, double Lon)[] SouthNapsPolygon = new (double, double)[] {
        (7.1051985, 125.6016998),
        (7.1056376, 125.5939556),
        (7.1026918, 125.5946747),
        (7.1019233, 125.5954307),
        (7.1009535, 125.5961867),
        (7.1008804, 125.5973114),
        (7.101411, 125.5984731),
        (7.1028564, 125.6000772),
        (7.1030028, 125.6006857),
        (7.1022526, 125.6010914),
        (7.0989225, 125.6011836),
        (7.0962328, 125.5999113),
        (7.0947873, 125.5986759),
        (7.093781, 125.5965923),
        (7.0943116, 125.5951357),
        (7.0960865, 125.5935131),
        (7.0971111, 125.5926465),
        (7.0973673, 125.5920564),
        (7.0968366, 125.5914848),
        (7.0951899, 125.5908764),
        (7.0926465, 125.5918352),
        (7.091567, 125.5939187),
        (7.0916768, 125.5967583),
        (7.0920244, 125.598344),
        (7.0922074, 125.6009807),
        (7.0910729, 125.6021424),
        (7.0897555, 125.6024743),
        (7.087816, 125.602087),
        (7.0870109, 125.6017367),
        (7.0863155, 125.5999113),
        (7.086096, 125.5977909),
        (7.086773, 125.5963342),
        (7.0882917, 125.59414),
        (7.0884381, 125.5933287),
        (7.0882002, 125.5924805),
        (7.0875598, 125.5919827),
        (7.0865717, 125.5917799),
        (7.08573, 125.5919274),
        (7.0848151, 125.5926465),
        (7.0841564, 125.5931259),
        (7.0826377, 125.59355),
        (7.0805883, 125.5940847),
        (7.0789781, 125.5946379),
        (7.0781547, 125.5941769),
        (7.0772947, 125.5929784),
        (7.075904, 125.591282),
        (7.0750623, 125.5898254),
        (7.0740193, 125.5891985),
        (7.0726835, 125.5892906),
        (7.0719882, 125.5898069),
        (7.0719699, 125.5909317),
        (7.0720431, 125.5924068),
        (7.0716954, 125.5942322),
        (7.0714941, 125.5951357),
        (7.0699388, 125.5973852),
        (7.06756, 125.5991737),
        (7.065785, 125.6006304),
        (7.0656935, 125.6023636),
        (7.0644858, 125.6053875),
        (7.0626925, 125.6063648),
        (7.0603503, 125.6059776),
        (7.0581544, 125.6051663),
        (7.0562514, 125.6052769),
        (7.0519328, 125.6067335),
        (7.0487487, 125.6080058),
        (7.0481448, 125.6092043),
        (7.0490598, 125.6107347),
        (7.0497552, 125.6112694),
        (7.0534882, 125.61138),
        (7.0551352, 125.6115644),
        (7.0576604, 125.6132055),
        (7.0616862, 125.6161188),
        (7.0629854, 125.6170407),
        (7.0635527, 125.6182945),
        (7.0645408, 125.6194746),
        (7.0651446, 125.6205809),
        (7.0669379, 125.6218163),
        (7.0680541, 125.6227014),
        (7.0694631, 125.6246559),
        (7.0702865, 125.6259097),
        (7.0730679, 125.6272741),
        (7.0758309, 125.627016),
        (7.0774412, 125.6268685),
        (7.0767275, 125.6134637),
        (7.0831318, 125.6125417),
        (7.0837723, 125.6124311),
        (7.0871391, 125.6105319),
        (7.0888193, 125.6102127),
        (7.088917, 125.6087036),
        (7.0889387, 125.6084193),
        (7.0905447, 125.6065713),
        (7.0914996, 125.6058715),
        (7.0921615, 125.6050513),
        (7.092932, 125.6045702),
        (7.093518, 125.6039032),
        (7.0943101, 125.6033127),
        (7.0962609, 125.6030075),
        (7.0980288, 125.6030897),
        (7.0996062, 125.6027882),
        (7.10151, 125.6031171),
        (7.1030059, 125.6030349),
        (7.1038762, 125.6023771),
        (7.1046377, 125.6017741),
        (7.1051985, 125.6016998)
    };

    private static readonly (double Lat, double Lon)[] CityCenterNapsPolygon = new (double, double)[] {
        (7.077492, 125.6277369),
        (7.0765814, 125.6134743),
        (7.0837874, 125.6124698),
        (7.0871152, 125.6103739),
        (7.0888369, 125.6101448),
        (7.0888524, 125.6084177),
        (7.0906281, 125.6065505),
        (7.0920795, 125.6051656),
        (7.0938742, 125.6035755),
        (7.0956962, 125.6029842),
        (7.0972866, 125.6031087),
        (7.0993315, 125.6027201),
        (7.1006944, 125.6029916),
        (7.1015964, 125.6030978),
        (7.103084, 125.6031923),
        (7.1046303, 125.6017521),
        (7.1051457, 125.6017521),
        (7.1058602, 125.6051636),
        (7.1064108, 125.6068517),
        (7.1078556, 125.6111177),
        (7.1090035, 125.6146742),
        (7.1107499, 125.6179448),
        (7.1143343, 125.6229972),
        (7.1155963, 125.6245695),
        (7.119368, 125.6279456),
        (7.1239347, 125.6321481),
        (7.1367049, 125.6472102),
        (7.1489176, 125.6602834),
        (7.1509875, 125.6625086),
        (7.1459032, 125.6644845),
        (7.1443852, 125.6629546),
        (7.121597, 125.6633404),
        (7.1089227, 125.6539701),
        (7.1043421, 125.6510765),
        (7.102686, 125.6503811),
        (7.1013749, 125.6509374),
        (7.0978557, 125.6502421),
        (7.0953025, 125.6469042),
        (7.0935083, 125.6422452),
        (7.0905964, 125.6420877),
        (7.0878361, 125.6392367),
        (7.0852138, 125.6311007),
        (7.0816255, 125.626998),
        (7.077492, 125.6277369)
    };

    private static readonly (double Lat, double Lon)[] NorthNapsPolygon = new (double, double)[] {
        (7.1058045, 125.5937659),
        (7.1053092, 125.6016775),
        (7.1053092, 125.6030252),
        (7.1071914, 125.6086156),
        (7.1080582, 125.6111113),
        (7.1094699, 125.6153042),
        (7.1096598, 125.615919),
        (7.1102046, 125.6169165),
        (7.1117301, 125.6190211),
        (7.1134372, 125.6214644),
        (7.1150899, 125.6237978),
        (7.1188673, 125.6272568),
        (7.1235345, 125.6314296),
        (7.1249529, 125.6326853),
        (7.129311, 125.63824),
        (7.1326436, 125.6425889),
        (7.1403485, 125.6506984),
        (7.1487795, 125.659669),
        (7.1507483, 125.6622981),
        (7.1538223, 125.6627747),
        (7.1574479, 125.6612654),
        (7.1569848, 125.6573453),
        (7.1624179, 125.6471012),
        (7.1614107, 125.6419331),
        (7.1622043, 125.6410409),
        (7.161838, 125.6362419),
        (7.1624771, 125.6328921),
        (7.1620908, 125.6307636),
        (7.1621423, 125.6291153),
        (7.1621938, 125.6285054),
        (7.1618204, 125.6272205),
        (7.1618976, 125.6256631),
        (7.1611378, 125.6235346),
        (7.1609447, 125.6224185),
        (7.1611894, 125.6210817),
        (7.1608803, 125.6201602),
        (7.1601463, 125.61968),
        (7.1597085, 125.618512),
        (7.1582018, 125.6176035),
        (7.1575039, 125.6154933),
        (7.1577768, 125.6126447),
        (7.1591873, 125.6108521),
        (7.1631738, 125.6097086),
        (7.1650137, 125.6089668),
        (7.1667002, 125.6067725),
        (7.1665469, 125.6027856),
        (7.164799, 125.5997568),
        (7.1634498, 125.5960172),
        (7.1532992, 125.5761473),
        (7.1509982, 125.5763766),
        (7.1490258, 125.5775744),
        (7.1485454, 125.5782625),
        (7.1474328, 125.5791289),
        (7.1449041, 125.5800719),
        (7.1434375, 125.5809893),
        (7.1418192, 125.5834868),
        (7.140656, 125.5846336),
        (7.1395433, 125.5845062),
        (7.1383043, 125.5835123),
        (7.1373181, 125.5820852),
        (7.1365342, 125.5811422),
        (7.135194, 125.5816519),
        (7.1339043, 125.5827987),
        (7.1313756, 125.5832574),
        (7.1303641, 125.5844807),
        (7.1298331, 125.586443),
        (7.1284928, 125.587284),
        (7.126394, 125.5866214),
        (7.1254078, 125.5859843),
        (7.1227641, 125.5864283),
        (7.120419, 125.5856406),
        (7.1197025, 125.5822926),
        (7.1183345, 125.5818987),
        (7.1172923, 125.584262),
        (7.1152077, 125.5870848),
        (7.1133186, 125.5880695),
        (7.110713, 125.5879382),
        (7.1092147, 125.5893825),
        (7.1079119, 125.5921396),
        (7.1058045, 125.5937659)
    };

    private static readonly (double Lat, double Lon)[] NorthOutmostNapsPolygon = new (double, double)[] {
        (7.1572824, 125.6571575),
        (7.1605614, 125.6509328),
        (7.1624197, 125.6474783),
        (7.162525, 125.6466646),
        (7.1617481, 125.6427077),
        (7.1616296, 125.6420573),
        (7.1624197, 125.6412079),
        (7.1621668, 125.6380257),
        (7.1619824, 125.6363454),
        (7.1623116, 125.6345669),
        (7.1626013, 125.6330273),
        (7.162417, 125.6317001),
        (7.1622195, 125.6302269),
        (7.1622985, 125.62882),
        (7.1621141, 125.6274662),
        (7.1619824, 125.6262054),
        (7.1619429, 125.6253294),
        (7.161482, 125.6241216),
        (7.1611923, 125.6229404),
        (7.1611265, 125.6221706),
        (7.1613503, 125.6213876),
        (7.1612713, 125.6207107),
        (7.1609553, 125.6200603),
        (7.1605339, 125.6197816),
        (7.1601256, 125.6193702),
        (7.1599808, 125.6188127),
        (7.1597701, 125.6183748),
        (7.1586248, 125.6177294),
        (7.1583088, 125.617517),
        (7.1579005, 125.6163093),
        (7.1575713, 125.6152475),
        (7.157782, 125.6138539),
        (7.1580191, 125.6127523),
        (7.1581112, 125.6123807),
        (7.1592569, 125.6110004),
        (7.1613771, 125.6104031),
        (7.1647731, 125.6093006),
        (7.1656047, 125.6086985),
        (7.1664623, 125.6074471),
        (7.1668488, 125.6068687),
        (7.1668722, 125.6049031),
        (7.1667434, 125.6026955),
        (7.1660992, 125.6015385),
        (7.1650553, 125.5998817),
        (7.1644813, 125.5983706),
        (7.1639777, 125.5970012),
        (7.1636028, 125.5959033),
        (7.1533387, 125.5760845),
        (7.1541961, 125.5759117),
        (7.1547791, 125.5759462),
        (7.1575685, 125.5750591),
        (7.1581493, 125.5746685),
        (7.1591667, 125.5735855),
        (7.1601956, 125.5722099),
        (7.1606871, 125.5711384),
        (7.1610758, 125.5704839),
        (7.1617045, 125.5696591),
        (7.1620361, 125.5687801),
        (7.1622761, 125.5683538),
        (7.1630077, 125.567651),
        (7.1632821, 125.5677086),
        (7.1634993, 125.5680197),
        (7.1638765, 125.5688492),
        (7.1642081, 125.5701166),
        (7.1643795, 125.5703124),
        (7.1645853, 125.5712111),
        (7.1648139, 125.5714185),
        (7.1649625, 125.5719369),
        (7.1655798, 125.5726668),
        (7.1662657, 125.573552),
        (7.166643, 125.5742373),
        (7.1672831, 125.5742604),
        (7.1684148, 125.5744101),
        (7.1701638, 125.5742719),
        (7.1731752, 125.5730541),
        (7.1753789, 125.5715438),
        (7.1756984, 125.5708413),
        (7.1750885, 125.5696704),
        (7.1742754, 125.5671824),
        (7.1736074, 125.5657774),
        (7.1723005, 125.5645187),
        (7.1715164, 125.5637577),
        (7.1709355, 125.563143),
        (7.1707613, 125.5618258),
        (7.1718649, 125.5599817),
        (7.1738397, 125.5576986),
        (7.1743334, 125.5557959),
        (7.1747691, 125.555152),
        (7.1756403, 125.5551227),
        (7.1788059, 125.5575229),
        (7.1803741, 125.5593963),
        (7.1823457, 125.5619429),
        (7.1841172, 125.5626747),
        (7.1854821, 125.5622571),
        (7.1865567, 125.560735),
        (7.1871956, 125.5594178),
        (7.1882991, 125.5591837),
        (7.1905353, 125.5600033),
        (7.1931258, 125.559974),
        (7.1967268, 125.5591544),
        (7.2001656, 125.5613205),
        (7.2043066, 125.5627255),
        (7.2070511, 125.5646574),
        (7.2100422, 125.5642368),
        (7.3421231, 125.5729596),
        (7.3451451, 125.5918942),
        (7.3473036, 125.5984234),
        (7.3380219, 125.6123523),
        (7.3324096, 125.6332457),
        (7.3328413, 125.6819968),
        (7.3333345, 125.690652),
        (7.3278612, 125.7058752),
        (7.3239921, 125.7104421),
        (7.3232372, 125.716912),
        (7.3177638, 125.7275682),
        (7.3148383, 125.7379888),
        (7.310686, 125.7372277),
        (7.3072886, 125.7312336),
        (7.3090816, 125.726952),
        (7.3037024, 125.7172473),
        (7.2986062, 125.7130609),
        (7.2964356, 125.713822),
        (7.2958694, 125.7171521),
        (7.2953031, 125.7176278),
        (7.2939819, 125.7155347),
        (7.2929438, 125.7147735),
        (7.2898294, 125.7114434),
        (7.2906403, 125.7063778),
        (7.2932856, 125.7001296),
        (7.2941926, 125.6974627),
        (7.2815704, 125.6888523),
        (7.2703084, 125.6816898),
        (7.2694769, 125.6848901),
        (7.2635345, 125.681385),
        (7.2549932, 125.6799372),
        (7.2535571, 125.6790594),
        (7.2528012, 125.6744114),
        (7.2512138, 125.6712873),
        (7.2509871, 125.6649714),
        (7.2515162, 125.6612377),
        (7.2485683, 125.6560563),
        (7.2452423, 125.6521702),
        (7.2415385, 125.6495795),
        (7.2379101, 125.6511796),
        (7.2353401, 125.6482841),
        (7.2310314, 125.6519416),
        (7.2273504, 125.6511796),
        (7.2276528, 125.6477507),
        (7.2253288, 125.6462073),
        (7.2244216, 125.6481884),
        (7.2223806, 125.6482646),
        (7.2221539, 125.6463597),
        (7.220264, 125.6465883),
        (7.2156528, 125.6452929),
        (7.213763, 125.6479599),
        (7.2103612, 125.6466645),
        (7.2108148, 125.6513887),
        (7.2077154, 125.6496362),
        (7.2041624, 125.6482646),
        (7.1832678, 125.6522269),
        (7.181287, 125.6564178),
        (7.1661668, 125.6581703),
        (7.1578493, 125.6609938),
        (7.1572824, 125.6571575)
    };

    private static bool IsPointInPolygon(double lat, double lon, (double Lat, double Lon)[] polygon)
    {
        if (polygon == null || polygon.Length < 3) return false;
        bool isInside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if (((polygon[i].Lon > lon) != (polygon[j].Lon > lon)) &&
                (lat < (polygon[j].Lat - polygon[i].Lat) * (lon - polygon[i].Lon) / (polygon[j].Lon - polygon[i].Lon) + polygon[i].Lat))
            {
                isInside = !isInside;
            }
        }
        return isInside;
    }

    private static double MinDistanceToPolygon(double lat, double lon, (double Lat, double Lon)[] polygon)
    {
        double minDist = double.MaxValue;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            double dist = PointToSegmentDistance(lat, lon, polygon[j].Lat, polygon[j].Lon, polygon[i].Lat, polygon[i].Lon);
            if (dist < minDist) minDist = dist;
        }
        return minDist;
    }

    private static double PointToSegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq == 0) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static string FindNearestRegion(double lat, double lon)
    {
        var regions = new (string Name, (double Lat, double Lon)[] Polygon)[]
        {
            ("South Naps", SouthNapsPolygon),
            ("City Center Naps", CityCenterNapsPolygon),
            ("North Naps", NorthNapsPolygon),
            ("North Outmost Naps", NorthOutmostNapsPolygon)
        };

        string nearest = regions[0].Name;
        double minDist = double.MaxValue;
        foreach (var (name, polygon) in regions)
        {
            double dist = MinDistanceToPolygon(lat, lon, polygon);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = name;
            }
        }
        return nearest;
    }

    private static string GetDavaoNorthSubRegion(ExcelDataReader.IExcelDataReader reader, Dictionary<string, int> headers)
    {
        var latStr = reader.GetValue(headers["DP/NAP LAT"])?.ToString()?.Trim() ?? "";
        var lonStr = reader.GetValue(headers["DP/NAP LONG"])?.ToString()?.Trim() ?? "";
        if (double.TryParse(latStr, out double lat) && double.TryParse(lonStr, out double lon))
        {
            if (IsPointInPolygon(lat, lon, SouthNapsPolygon))
                return "South Naps";
            if (IsPointInPolygon(lat, lon, CityCenterNapsPolygon))
                return "City Center Naps";
            if (IsPointInPolygon(lat, lon, NorthNapsPolygon))
                return "North Naps";
            if (IsPointInPolygon(lat, lon, NorthOutmostNapsPolygon))
                return "North Outmost Naps";
            return FindNearestRegion(lat, lon);
        }
        return "South Naps";
    }

    public async Task<string> ProcessAndZipAsync(Stream xlsxStream, string originalFileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var groupedData = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var requiredCols = new[] { "DP", "DP/NAP LAT", "DP/NAP LONG", "S_SP", "S_Total", "CFS Area", "CFS Cluster", "DP Location", "Tech" };

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

                    bool isSouthMindanao = cfsArea.Equals("SOUTH MINDANAO 1", StringComparison.OrdinalIgnoreCase);
                    
                    bool is8 = sTotalRaw == "8" || sTotalRaw == "8.0" || sTotalRaw == "8.00";
                    bool is16 = sTotalRaw == "16" || sTotalRaw == "16.0" || sTotalRaw == "16.00";

                    var tech = reader.GetValue(headers["Tech"])?.ToString()?.Trim() ?? "";
                    bool isDsl = tech.Equals("ADSL", StringComparison.OrdinalIgnoreCase) ||
                                 tech.Equals("ADSL/VDSL", StringComparison.OrdinalIgnoreCase) ||
                                 tech.Equals("VDSL", StringComparison.OrdinalIgnoreCase);

                    if (isSouthMindanao && (is8 || is16))
                    {
                        var cfsCluster = reader.GetValue(headers["CFS Cluster"])?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(cfsCluster))
                        {
                            cfsCluster = "UNKNOWN_CLUSTER";
                        }

                        var ports = is8 ? "8" : "16";
                        var groupKey = $"{cfsCluster} - {ports} PORTS";

                        if (cfsCluster.Equals("DAVAO NORTH", StringComparison.OrdinalIgnoreCase))
                        {
                            groupKey += " - " + GetDavaoNorthSubRegion(reader, headers);
                        }

                        if (!groupedData.ContainsKey(groupKey))
                        {
                            groupedData[groupKey] = new List<Dictionary<string, string>>();
                        }

                        var rowData = new Dictionary<string, string>();
                        foreach (var req in requiredCols)
                        {
                            rowData[req] = reader.GetValue(headers[req])?.ToString()?.Trim() ?? "";
                        }
                        groupedData[groupKey].Add(rowData);
                    }

                    // DSL extraction (independent of S_Total)
                    if (isSouthMindanao && isDsl)
                    {
                        var cfsCluster = reader.GetValue(headers["CFS Cluster"])?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(cfsCluster))
                        {
                            cfsCluster = "UNKNOWN_CLUSTER";
                        }

                        var groupKey = $"{cfsCluster} - DSL";

                        if (cfsCluster.Equals("DAVAO NORTH", StringComparison.OrdinalIgnoreCase))
                        {
                            groupKey += " " + GetDavaoNorthSubRegion(reader, headers);
                        }

                        if (!groupedData.ContainsKey(groupKey))
                        {
                            groupedData[groupKey] = new List<Dictionary<string, string>>();
                        }

                        var rowData = new Dictionary<string, string>();
                        foreach (var req in requiredCols)
                        {
                            rowData[req] = reader.GetValue(headers[req])?.ToString()?.Trim() ?? "";
                        }
                        groupedData[groupKey].Add(rowData);
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
                        var colName = requiredCols[i];
                        if (colName.Equals("DP/NAP LAT", StringComparison.OrdinalIgnoreCase)) colName = "Latitude";
                        if (colName.Equals("DP/NAP LONG", StringComparison.OrdinalIgnoreCase)) colName = "Longitude";
                        outWs.Cell(1, i + 1).Value = colName;
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
