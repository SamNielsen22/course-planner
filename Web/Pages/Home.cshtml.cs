using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

[OutputCache(Duration = 60)]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
public class HomeModel : PageModel
{
    public void OnGet() { }
}
