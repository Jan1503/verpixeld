using System.Text;

namespace verpixeld.WebApi;

public static class WebUIProvider
{
    /// <summary>index.html with content-versioned script and stylesheet URLs (<see cref="AssetVersioning"/>).</summary>
    public static string GetIndexHtml()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var htmlPath = Path.Combine(webRoot, "index.html");
        if (File.Exists(htmlPath))
            // UTF-8 explicitly: the page has emoji
            return AssetVersioning.Apply(File.ReadAllText(htmlPath, Encoding.UTF8), webRoot);

        Console.WriteLine("[SERVER] wwwroot/index.html not found, serving the built-in fallback page");
        return GetEmbeddedHtml();
    }

    private static string GetEmbeddedHtml()
    {
        return """
               <!DOCTYPE html>
               <html lang="en">
               <head>
                   <meta charset="UTF-8">
                   <meta name="viewport" content="width=device-width, initial-scale=1.0">
                   <title>RGB Display Control</title>
                   <link rel="stylesheet" href="/styles.css">
               </head>
               <body>
                   <div class="container">
                       <h1>?? RGB Display Control Panel</h1>
                       <p class="subtitle">Real-time control interface for your LED matrix display</p>
                       
                       <div class="status-bar">
                           <div class="status-item">
                               <span class="status-label">IP Address:</span>
                               <span class="status-value" id="ip-address">Loading...</span>
                           </div>
                           <div class="status-item">
                               <span class="status-label">Stream Status:</span>
                               <span class="status-value">
                                   <span class="badge inactive" id="stream-status">Inactive</span>
                               </span>
                           </div>
                           <div class="status-item">
                               <span class="status-label">Display Resolution:</span>
                               <span class="status-value" id="resolution">384x192</span>
                           </div>
                           <div class="status-item">
                               <span class="status-label">Active Filters:</span>
                               <span class="status-value" id="filter-count">0</span>
                           </div>
                           <div class="status-item">
                               <span class="status-label">Uptime:</span>
                               <span class="status-value" id="uptime">-</span>
                           </div>
                       </div>

                       <div id="message" class="message"></div>

                       <div class="section">
                           <h2>?? Mode Control</h2>
                           <button onclick="setMode('local')" class="btn">?? Start Local Mode</button>
                           <button onclick="setMode('stop')" class="btn btn-danger">?? Stop Local Mode</button>
                       </div>

                       <div class="section">
                           <h2>? Active Filters</h2>
                           <div id="active-filters" class="filter-list">
                               <p class="text-muted">Loading...</p>
                           </div>
                           <div style="display: flex; gap: 10px; margin-top: 12px;">
                               <button onclick="showFilterPicker()" class="btn">? Add Filter</button>
                               <button onclick="clearAllFilters()" class="btn btn-danger">??? Clear All Filters</button>
                           </div>
                       </div>

                       <!-- Filter Form Modal -->
                       <div id="filter-form-container" class="modal" style="display: none;">
                           <div class="modal-content">
                               <h2 id="filter-form-title">Add Filter</h2>
                               <div id="filter-form"></div>
                           </div>
                       </div>
                   </div>

                   <script src="/app.js"></script>
               </body>
               </html>
               """;
    }
}
