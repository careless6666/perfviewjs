// <copyright file="Startup.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace PerfViewJS
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Http;

    public sealed class Startup
    {
        private const string JsonContentType = "application/json; charset=UTF-8";

        private const string JavaScriptContentType = "application/javascript; charset=UTF-8";

        private const string CSSContentType = "text/css; charset=UTF-8";

        private const string HTMLContentType = "text/html; charset=UTF-8";

        private const string AcceptEncoding = "Accept-Encoding";

        private const string ContentEncoding = "Content-Encoding";

        private const string Brotli = "br";

        private const string GZip = "gzip";

        private const string JSExtension = ".js";

        private const string CSSExtension = ".css";

        private const string HTMLExtension = ".html";

        private readonly HashSet<string> perfviewJSSupportedFileExtensions =
            new HashSet<string> { "*.etl", "*.btl", "*.netperf", "*.nettrace" };

        private readonly JsonSerializerOptions jsonSerializerSettings = new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly DeserializedDataCache deserializedDataCache =
            new DeserializedDataCache(new CallTreeDataCache(new MemoryCacheOptionsConfig()),
                new CacheExpirationTimeProvider());

        private readonly string contentRoot;

        private readonly string indexFile;

        public static string dataDirectoryListingRoot;

        private readonly string defaultAuthorizationHeader;

        public Startup()
        {
            dataDirectoryListingRoot = "/Users/alexander.usov/Documents/github/perfviewjs";
        }

        public Startup(string contentRootPath, string dataDirectoryListingRoot, string defaultAuthorizationHeader)
        {
            this.contentRoot = Path.Combine(contentRootPath, "spa", "build");
            this.indexFile = Path.GetFullPath(Path.Join(this.contentRoot, "index.html"));
            dataDirectoryListingRoot = Path.GetFullPath(dataDirectoryListingRoot);
            this.defaultAuthorizationHeader = defaultAuthorizationHeader;
        }

        internal enum Compression
        {
            /// <summary>
            /// Brotli compression
            /// </summary>
            Brotli,

            /// <summary>
            /// GZip compression
            /// </summary>
            GZip,

            /// <summary>
            /// No compression
            /// </summary>
            None,
        }

        public Task HandleRequest(HttpContext context)
        {
            var request = context.Request;
            var acceptEncoding = request.Headers[AcceptEncoding].ToString() ?? string.Empty;

            return this.HandleRequestInner(request.Path.Value, request.Query, context.Response, context.RequestAborted,
                acceptEncoding.Contains(Brotli) ? Compression.Brotli :
                acceptEncoding.Contains(GZip) ? Compression.GZip : Compression.None);
        }

        internal async Task HandleRequestInner(string requestPath, IQueryCollection queryCollection,
            HttpResponse response, CancellationToken requestAborted, Compression compression)
        {
            if (requestPath.StartsWith("/api"))
            {
                if (requestPath.StartsWith(@"/api/eventdata"))
                {
                    var controller = new EventViewerController(this.deserializedDataCache,
                        new EventViewerModel(dataDirectoryListingRoot, queryCollection));
                    await WriteJsonResponse(response, this.jsonSerializerSettings, await controller.EventsAPI(),
                        compression);
                }
                else
                {
                    
                }
            }
            else if (requestPath.StartsWith("/ui"))
            {
                await SendIndexFile(response, requestAborted, this.indexFile, compression);
            }
            else
            {
                var fullPath = Path.GetFullPath(Path.Join(this.contentRoot.AsSpan(), requestPath.AsSpan(1)));
                if (fullPath.StartsWith(this.contentRoot) && File.Exists(fullPath))
                {
                    response.Headers["Cache-Control"] = "public, max-age=31536000";

                    var ext = Path.GetExtension(fullPath);
                    if (ext.EndsWith(JSExtension))
                    {
                        response.ContentType = JavaScriptContentType;
                    }
                    else if (ext.EndsWith(CSSExtension))
                    {
                        response.ContentType = CSSContentType;
                    }
                    else if (ext.EndsWith(HTMLExtension))
                    {
                        response.ContentType = HTMLContentType;
                    }

                    await SendPotentiallyCompressedFileAsync(response, requestAborted, fullPath, compression);
                }
                else
                {
                    if (requestPath.Equals("/"))
                    {
                        await SendIndexFile(response, requestAborted, this.indexFile, compression);
                    }
                    else
                    {
                        response.StatusCode = 404;
                        await response.WriteAsync("404 Not Found", requestAborted);
                    }
                }
            }
        }

        private static async Task SendPotentiallyCompressedFileAsync(HttpResponse response,
            CancellationToken requestAborted, string file, Compression compression)
        {
            switch (compression)
            {
                case Compression.Brotli:
                    await SendCompressedOrUncompressedFileAsync(file, Brotli, response, requestAborted);
                    break;
                case Compression.GZip:
                    await SendCompressedOrUncompressedFileAsync(file, GZip, response, requestAborted);
                    break;
                default:
                    await SendFileAsync(file, response, requestAborted);
                    break;
            }
        }

        private static async Task SendIndexFile(HttpResponse response, CancellationToken requestAborted,
            string indexFile, Compression compression)
        {
            response.ContentType = HTMLContentType;
            await SendPotentiallyCompressedFileAsync(response, requestAborted, indexFile, compression);
        }

        private static async Task WriteJsonResponse<T>(HttpResponse response, JsonSerializerOptions settings, T data,
            Compression compression)
        {
            var jsonUtf8Bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, settings));

            switch (compression)
            {
                case Compression.Brotli:
                    await WriteBrotliCompressedDynamicResponse(jsonUtf8Bytes, response);
                    break;
                case Compression.GZip:
                    await WriteGZipCompressedDynamicResponse(jsonUtf8Bytes, response);
                    break;
                default:
                    response.ContentLength = jsonUtf8Bytes.Length;
                    await response.Body.WriteAsync(jsonUtf8Bytes);
                    break;
            }
        }

        private static async Task WriteGZipCompressedDynamicResponse(byte[] input, HttpResponse response)
        {
            response.ContentLength = input.Length;
            response.Headers[ContentEncoding] = GZip;
            await using var gz = new GZipStream(response.Body, CompressionLevel.Fastest);
            await gz.WriteAsync(input, 0, input.Length);
        }

        private static Task WriteBrotliCompressedDynamicResponse(ReadOnlySpan<byte> input, HttpResponse response)
        {
            byte[] output = null;
            var arrayPool = ArrayPool<byte>.Shared;

            try
            {
                output = arrayPool.Rent(BrotliEncoder.GetMaxCompressedLength(input.Length));
                if (BrotliEncoder.TryCompress(input, output, out var bytesWritten, 4, 22))
                {
                    response.ContentLength = bytesWritten;
                    response.Headers[ContentEncoding] = Brotli;
                    return response.Body.WriteAsync(output, 0, bytesWritten);
                }
                else
                {
                    return TryCompressFalse();
                }
            }
            finally
            {
                if (output != null)
                {
                    arrayPool.Return(output);
                }
            }
        }

        private static async Task SendFileAsync(string file, HttpResponse response, CancellationToken cancellationToken)
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
            long remainingBytes = fs.Length;
            response.ContentLength = remainingBytes;

            byte[] buffer = null;
            var arrayPool = ArrayPool<byte>.Shared;

            try
            {
                buffer = arrayPool.Rent(81920);

                int bytesRead;

                while ((bytesRead =
                           await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    int min = (int)Math.Min(remainingBytes, bytesRead);
                    await response.Body.WriteAsync(buffer, 0, min, cancellationToken).ConfigureAwait(false);
                    remainingBytes -= min;

                    if (remainingBytes == 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (buffer != null)
                {
                    arrayPool.Return(buffer);
                }
            }
        }

        private static async Task SendCompressedOrUncompressedFileAsync(string file, string compressionExtension,
            HttpResponse response, CancellationToken requestAborted)
        {
            var compressedFile = file + "." + compressionExtension;
            if (File.Exists(compressedFile))
            {
                response.Headers[ContentEncoding] = compressionExtension;
                await SendFileAsync(compressedFile, response, requestAborted);
            }
            else
            {
                await SendFileAsync(file, response, requestAborted);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task TryCompressFalse()
        {
            throw new Exception("TryCompress returned false.");
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors();
            services.AddControllers();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseRouting();

            app.UseCors(c =>
            {
                c.AllowAnyHeader();
                c.AllowAnyOrigin();
                c.AllowAnyMethod();
            });

            app.UseEndpoints(e =>
            {
                e.MapControllers();
                //     e.MapGet("api/traceinfo",
                //         async context =>
                //         {
                //             var controller = new StackViewerController(deserializedDataCache,
                //                 new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                //
                //             await WriteJsonResponse(context.Response, jsonSerializerSettings,
                //                 await controller.GetTraceInfoAPI(), Compression.None);
                //         });

                e.MapGet("api/eventliston", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));

                    await WriteJsonResponse(context.Response, this.jsonSerializerSettings,
                        await controller.EventListAPIOrderedByName(), Compression.None);
                });

                e.MapGet("api/eventlistos", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    await WriteJsonResponse(context.Response, this.jsonSerializerSettings,
                        await controller.EventListAPIOrderedByStackCount(), Compression.None);
                });
                e.MapGet("api/processchooser", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        await controller.ProcessChooserAPI(), Compression.None);
                });

                e.MapGet("api/processinfo", async context =>
                {
                    var processIndexString = (string)context.Request.Query["processIndex"] ?? string.Empty;
                    var processIndex = -1;
                    if (!string.IsNullOrEmpty(processIndexString))
                    {
                        int.TryParse(processIndexString, out processIndex);
                    }
                    
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));

                    await WriteJsonResponse(context.Response, this.jsonSerializerSettings,
                        await controller.DetailedProcessInfoAPI(processIndex), Compression.None);
                });

                e.MapGet("api/modulelist", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    await WriteJsonResponse(context.Response, this.jsonSerializerSettings, await controller.GetModulesAPI(),
                        Compression.None);
                });

                e.MapGet("api/callerchildren", async context =>
                    {
                        var controller = new StackViewerController(deserializedDataCache,
                            new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                        
                        var name = (string)context.Request.Query["name"] ?? string.Empty;
                        var path = (string)context.Request.Query["path"] ?? string.Empty;

                        await WriteJsonResponse(context.Response, jsonSerializerSettings,
                            await controller.CallerChildrenAPI(name, path), Compression.None);
                        
                    });

                e.MapGet("api/treenode", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    var name = (string)context.Request.Query["name"] ?? string.Empty;
                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        await controller.TreeNodeAPI(name), Compression.None);
                });

                e.MapGet("api/hotspots", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    await WriteJsonResponse(context.Response, jsonSerializerSettings, await controller.HotspotsAPI(),
                        Compression.None);
                });

                e.MapGet("api/drillinto", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    bool exclusive = context.Request.Path.ToString().StartsWith(@"/api/drillinto/exclusive");

                    var name = (string)context.Request.Query["name"] ?? string.Empty;
                    var path = (string)context.Request.Query["path"] ?? string.Empty;

                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        await controller.DrillIntoAPI(exclusive, name, path), Compression.None);
                });

                e.MapGet("api/lookupwarmsymbols", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    var minCountString = (string)context.Request.Query["minCount"] ?? string.Empty;
                    var minCount = 50;
                    if (!string.IsNullOrEmpty(minCountString))
                    {
                        int.TryParse(minCountString, out minCount);
                    }

                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        await controller.LookupWarmSymbolsAPI(minCount), Compression.None);
                });

                e.MapGet("api/lookupsymbol", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    var moduleIndexString = (string)context.Request.Query["moduleIndex"] ?? string.Empty;
                    var moduleIndex = -1;
                    if (!string.IsNullOrEmpty(moduleIndexString))
                    {
                        int.TryParse(moduleIndexString, out moduleIndex);
                    }

                    var resp = await controller.LookupSymbolAPI(moduleIndex);
                    
                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        resp, Compression.None);
                });
                
                e.MapGet("api/lookupymbols", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    var moduleIndicesString = (string)context.Request.Query["moduleIndices"] ?? string.Empty;
                    int[] moduleIndices = null;
                    if (!string.IsNullOrEmpty(moduleIndicesString))
                    {
                        var split = moduleIndicesString.Split(',');
                        moduleIndices = new int[split.Length];
                        for (int i = 0; i < split.Length; ++i)
                        {
                            moduleIndices[i] = int.Parse(split[i]);
                        }
                    }

                    await WriteJsonResponse(context.Response, jsonSerializerSettings,
                        await controller.LookupSymbolsAPI(moduleIndices), Compression.None);
                });

                e.MapGet("api/getsource", async context =>
                {
                    var controller = new StackViewerController(deserializedDataCache,
                        new StackViewerModel(dataDirectoryListingRoot, context.Request.Query));
                    
                    var name = (string)context.Request.Query["name"] ?? string.Empty;
                    var path = (string)context.Request.Query["path"] ?? string.Empty;
                    var authorizationHeader = (string)context.Request.Query["authorizationHeader"] ??
                                              this.defaultAuthorizationHeader;

                    await WriteJsonResponse(context.Response, this.jsonSerializerSettings,
                        await controller.GetSourceAPI(name, path, authorizationHeader), Compression.None);
                });
            });
        }
    }
}