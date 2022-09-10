using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace PerfViewJS;

[Route("api")]
public class ApiController: Controller
{
    private readonly string contentRoot;

    private readonly string indexFile;

    private readonly string dataDirectoryListingRoot;

    private readonly string defaultAuthorizationHeader;
    
    private readonly HashSet<string> perfviewJSSupportedFileExtensions =
        new HashSet<string> { "*.etl", "*.btl", "*.netperf", "*.nettrace" };
    
    private readonly DeserializedDataCache deserializedDataCache =
        new DeserializedDataCache(new CallTreeDataCache(new MemoryCacheOptionsConfig()),
            new CacheExpirationTimeProvider());

    public ApiController()
    {
        dataDirectoryListingRoot = Path.GetFullPath(Startup.dataDirectoryListingRoot);
    }
    
    [HttpGet("datadirectorylisting")]
    public IActionResult Getdatadirectorylisting()
    {
        if (string.IsNullOrEmpty(Startup.dataDirectoryListingRoot))
        {
            return Ok("PerfViewJS_DataRoot not set");
        }
        else
        {
            var list = new List<string>();
            foreach (var item in this.perfviewJSSupportedFileExtensions)
            {
                var files = Directory.EnumerateFiles(this.dataDirectoryListingRoot, item).OrderByDescending(t => t);
                foreach (var file in files)
                {
                    list.Add(Path.GetFileName(file));
                }
            }

            return Ok(list);
        }
    }

    [HttpGet("traceinfo")]
    public async Task<IActionResult> GetTrace([FromQuery]TraceInfoModel model)
    {
        var controller = new StackViewerController(deserializedDataCache,
            new StackViewerModel(dataDirectoryListingRoot, new QueryCollection(new Dictionary<string, StringValues>()
                {
                    ["Filename"] = model.Filename
                })
            ));
        
        return Ok(await controller.GetTraceInfoAPI());
    }
}