using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace SlotAd_Globe.Models;

public class DailyProductivityUploadModel
{
    [Required(ErrorMessage = "Please upload a CSV or XLSX file.")]
    public IFormFile ProductivityFile { get; set; }
}