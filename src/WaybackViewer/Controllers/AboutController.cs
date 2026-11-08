using Microsoft.AspNetCore.Mvc;

namespace WaybackViewer.Controllers;

public class AboutController : Controller
{
    [HttpGet("about")]
    public IActionResult Index()
    {
        var version = typeof(AboutController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return View(model: version);
    }
}
