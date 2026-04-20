using System.ComponentModel.DataAnnotations;

namespace SlotAd_Globe.Models;

public class CsvUploadViewModel
{
    [Required(ErrorMessage = "Please select a CSV or XLSX file.")]
    [Display(Name = "Report File")]
    public IFormFile? CsvFile { get; set; }
}
