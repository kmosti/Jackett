﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CsQuery;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig.Bespoke;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Indexers
{
    /// <summary>s
    /// Provider for ResurrectTheNet Private Tracker
    /// </summary>
    public class ResurrectTheNet: BaseCachingWebIndexer
    {
        private string LoginUrl => SiteLink + "rbg_login_new.php";
        private string indexPageUrl => SiteLink + "index.php";
        private string changeProfileUrl => SiteLink + "account_change.php?style=22&returnto={returnpage}"; // unsure how to determine
        private string LoginCheckUrl => SiteLink + "index.php?page=login";
        private string SearchUrl => SiteLink + "index.php?page=torrents";
        private string TorrentCommentUrl => SiteLink + "index.php?page=torrent-details&id={id}#comments";
        private string TorrentDescriptionUrl => SiteLink + "index.php?page=torrent-details&id={id}";
        private string TorrentDownloadUrl => SiteLink + "download.php?id={id}&f={filename}";
        private bool Latency => ConfigData.Latency.Value;
        private bool DevMode => ConfigData.DevMode.Value;
        private bool CacheMode => ConfigData.HardDriveCache.Value;
        private static string Directory => Path.Combine(Path.GetTempPath(), "Jackett", MethodBase.GetCurrentMethod().DeclaringType?.Name);

        private readonly Dictionary<string, string> _emulatedBrowserHeaders = new Dictionary<string, string>();
        private CQ _fDom;
        private ConfigurationDataResurrecttheNet ConfigData => (ConfigurationDataResurrecttheNet)configData;

        public ResurrectTheNet(IIndexerConfigurationService configService, Utils.Clients.WebClient w, Logger l, IProtectionService ps)
            : base(
                name: "Resurrect the net",
                description: "Resurrect the net is a Private site for MOVIES and TV",
                link: "http://resurrectthe.net/",
                caps: new TorznabCapabilities(),
                configService: configService,
                client: w,
                logger: l,
                p: ps,
                configData: new ConfigurationDataResurrecttheNet())
        {
            Encoding = Encoding.GetEncoding("iso-8859-1");
            Language = "en-us";
            Type = "private";

            TorznabCaps.SupportsImdbMovieSearch = true;

            AddCategoryMapping("category=11", TorznabCatType.MoviesHD, "Movies X264");
            AddCategoryMapping("category=15", TorznabCatType.MoviesSD, "Movies XviD");
            AddCategoryMapping("category=24", TorznabCatType.TVSD, "Episodes XviD");
            AddCategoryMapping("category=23", TorznabCatType.TVHD, "Episodes X264");
            AddCategoryMapping("category=1", TorznabCatType.PC, "Appz/Misc");
            AddCategoryMapping("category=7", TorznabCatType.TVAnime, "Anime");
            AddCategoryMapping("category=4", TorznabCatType.AudioAudiobook, "Books/Audio books");
            AddCategoryMapping("category=19", TorznabCatType.TVSport, "Sport");
        }

        /// <summary>
        /// Configure our FADN Provider
        /// </summary>
        /// <param name="configJson">Our params in Json</param>
        /// <returns>Configuration state</returns>
        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            // Retrieve config values set by Jackett's user
            LoadValuesFromJson(configJson);

            // Check & Validate Config
            ValidateConfig();

            // Setting our data for a better emulated browser (maximum security)
            // TODO: Encoded Content not supported by Jackett at this time
            // emulatedBrowserHeaders.Add("Accept-Encoding", "gzip, deflate");

            // If we want to simulate a browser
            if (ConfigData.Browser.Value)
            {
                // Clean headers
                _emulatedBrowserHeaders.Clear();

                // Inject headers
                _emulatedBrowserHeaders.Add("Accept", ConfigData.HeaderAccept.Value);
                _emulatedBrowserHeaders.Add("Accept-Language", ConfigData.HeaderAcceptLang.Value);
                _emulatedBrowserHeaders.Add("DNT", Convert.ToInt32(ConfigData.HeaderDnt.Value).ToString());
                _emulatedBrowserHeaders.Add("Upgrade-Insecure-Requests", Convert.ToInt32(ConfigData.HeaderUpgradeInsecure.Value).ToString());
                _emulatedBrowserHeaders.Add("User-Agent", ConfigData.HeaderUserAgent.Value);
                _emulatedBrowserHeaders.Add("Referer", LoginUrl);
            }

            await DoLogin();

            return IndexerConfigurationStatus.RequiresTesting;
        }

        /// <summary>
        /// Perform login to racker
        /// </summary>
        /// <returns></returns>
        private async Task DoLogin()
        {
            // Build WebRequest for index
            var myIndexRequest = new Utils.Clients.WebRequest()
            {
                Type = RequestType.GET,
                Url = SiteLink,
                Headers = _emulatedBrowserHeaders,
                Encoding = Encoding
            };

            // Get index page for cookies
            Output("\nGetting index page (for cookies).. with " + SiteLink);
            var indexPage = await webclient.GetString(myIndexRequest);

            // Building login form data
            var pairs = new Dictionary<string, string> {
                { "uid", ConfigData.Username.Value },
                { "pwd", ConfigData.Password.Value }
            };

            // Build WebRequest for login
            var myRequestLogin = new Utils.Clients.WebRequest()
            {
                Type = RequestType.GET,
                Url = LoginUrl,
                Headers = _emulatedBrowserHeaders,
                Cookies = indexPage.Cookies,
                Referer = SiteLink,
                Encoding = Encoding
            };

            // Get login page -- (not used, but simulation needed by tracker security's checks)
            LatencyNow();
            Output("\nGetting login page (user simulation).. with " + LoginUrl);
            await webclient.GetString(myRequestLogin);

            // Build WebRequest for submitting authentification
            var request = new Utils.Clients.WebRequest()
            {
                PostData = pairs,
                Referer = LoginUrl,
                Type = RequestType.POST,
                Url = LoginCheckUrl,
                Headers = _emulatedBrowserHeaders,
                Cookies = indexPage.Cookies,
                Encoding = Encoding
            };

            // Perform loggin
            LatencyNow();
            Output("\nPerform login.. with " + LoginCheckUrl);
            var response = await webclient.GetString(request);

            // Test if we are logged in
            await ConfigureIfOK(response.Cookies, response.Cookies != null && response.Cookies.Contains("uid="), () =>
            {
                // Default error message
                var message = "Error during attempt !";
                // Parse redirect header
                var redirectTo = response.RedirectingTo;

                // Oops, unable to login
                Output("-> Login failed: " + message, "error");
                throw new ExceptionWithConfigData("Login failed: " + message, configData);
            });

            Output("Changing layout to retro to ensure the expected DOM is loaded");
            /* This request will change the default styling to the "retro" theme,
             * because the parsing will not work on other themes as they 
             * present a different DOM
             */
            var stylingrequest = new Utils.Clients.WebRequest()
            {
                Referer = indexPageUrl,
                Type = RequestType.GET,
                Url = changeProfileUrl.Replace("{returnpage}", "%2Findex.php"),
                Headers = _emulatedBrowserHeaders,
                Cookies = indexPage.Cookies,
                Encoding = Encoding
            };

            var styleRequest = await webclient.GetString(stylingrequest);
            Output("changed styling to retro");

            Output("\nCookies saved for future uses...");
            ConfigData.CookieHeader.Value = indexPage.Cookies + " " + response.Cookies + " ts_username=" + ConfigData.Username.Value; // todo check

            Output("\n-> Login Success\n");
        }

        /// <summary>
        /// Check logged-in state for provider
        /// </summary>
        /// <returns></returns>
        private async Task CheckLogin()
        {
            // Checking ...
            Output("\n-> Checking logged-in state....");
            var loggedInCheck = await RequestStringWithCookies(SearchUrl);
            if (!loggedInCheck.Content.Contains("logout.php"))
            {
                // Cookie expired, renew session on provider
                Output("-> Not logged in, login now...\n");

                await DoLogin();
            }
            else
            {
                // Already logged, session active
                Output("-> Already logged in, continue...\n");
            }
        }

        /// <summary>
        /// Execute our search query
        /// </summary>
        /// <param name="query">Query</param>
        /// <returns>Releases</returns>
        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            var torrentRowList = new List<CQ>();
            var exactSearchTerm = query.GetQueryString();
            var searchUrl = SearchUrl;

            // Check login before performing a query
            await CheckLogin();

            // Check cache first so we don't query the server (if search term used or not in dev mode)
            if (!DevMode && !string.IsNullOrEmpty(exactSearchTerm))
            {
                lock (cache)
                {
                    // Remove old cache items
                    CleanCache();

                    // Search in cache
                    var cachedResult = cache.FirstOrDefault(i => i.Query == exactSearchTerm);
                    if (cachedResult != null)
                        return cachedResult.Results.Select(s => (ReleaseInfo)s.Clone()).ToArray();
                }
            }

            var SearchTerms = new List<string> { exactSearchTerm };

            // duplicate search without diacritics
            var baseSearchTerm = StringUtil.RemoveDiacritics(exactSearchTerm);
            if (baseSearchTerm != exactSearchTerm)
                SearchTerms.Add(baseSearchTerm);

            foreach (var searchTerm in SearchTerms)
            {
                // Build our query
                var request = BuildQuery(searchTerm, query, searchUrl);

                // Getting results & Store content
                var response = await RequestStringWithCookiesAndRetry(request, ConfigData.CookieHeader.Value);
                _fDom = response.Content;

                try
                {
                    var firstPageRows = FindTorrentRows();

                    // Add them to torrents list
                    torrentRowList.AddRange(firstPageRows.Select(fRow => fRow.Cq()));

                    // If pagination available
                    int nbResults;
                    int pageLinkCount;
                    nbResults = 1;
                    pageLinkCount = 1;

                    // Check if we have a minimum of one result
                    if (firstPageRows.Length > 1)
                    {
                        // Retrieve total count on our alone page
                        nbResults = firstPageRows.Count();
                    }
                    else
                    {
                        // Check if no result
                        if (torrentRowList.Count == 0)
                        {
                            // No results found
                            Output("\nNo result found for your query, please try another search term ...\n", "info");

                            // No result found for this query
                            break;
                        }
                    }

                    Output("\nFound " + nbResults + " result(s) (+/- " + firstPageRows.Length + ") in " + pageLinkCount + " page(s) for this query !");
                    Output("\nThere are " + firstPageRows.Length + " results on the first page !");

                    // Loop on results

                    foreach (var tRow in torrentRowList)
                    {
                        // ICollection<CsQuery.INodeList> childNodes = tRow.Elements.First().ChildNodes as ICollection<CsQuery.INodeList>;
                        // int childElements = tRow.Elements.First().ChildNodes.Count;
                        int childNodeCount = 0;
                        using (IEnumerator<IDomObject> enumerator = tRow.Elements.First().ChildNodes.GetEnumerator()){
                            while (enumerator.MoveNext())
                            {
                                childNodeCount++;
                            }
                        }
                        if (childNodeCount != 20 )
                        { 
                            Output("skipping a row with count != 20 (e.g row separators)");
                            continue;
                        }
                        Output("Torrent #" + (releases.Count + 1));

                        // ID
                        string element2 = "";
                        try
                        {
                            element2 = tRow.Find("td:eq(2) > a:eq(0)").Attr("href").Split('=').Last();
                        }
                        catch ( Exception err ) {
                            Output(err.ToString());
                            Output(tRow.Find("td:eq(4) > a:eq(0)").Attr("href").Split('=').Last());
                        }
                        var id = tRow.Find("td:eq(2) > a:eq(0)").Attr("href").Split('=').Last();
                        Output("ID: " + id);

                        // Release Name
                        var name = tRow.Find("td:eq(2) > a:eq(0)").Text();

                        // Category
                        var categoryId = tRow.Find("td:eq(0) > a:eq(0)").Attr("href").Split('=').Last();
                        var categoryName = tRow.Find("td:eq(0) > a:eq(0) > img:eq(0)").Attr("title");

                        Output("Category: " + categoryName);

                        // Seeders
                        var seeders = ParseUtil.CoerceInt(tRow.Find("td:eq(6) > a").Text());
                        Output("Seeders: " + seeders);

                        // Leechers
                        var leechers = ParseUtil.CoerceInt(tRow.Find("td:eq(7) > a").Text());
                        Output("Leechers: " + leechers);

                        // Completed
                        var completedCell = tRow.Find("td:eq(7) > a").Text();
                        if ( completedCell == "---" )
                        {
                            completedCell = "0";
                        }
                        var completed = ParseUtil.CoerceInt(completedCell);
                        Output("Completed: " + completed);

                        // Files
                        var files = 1;
                        Output("Files: " + files);

                        // Size
                        var humanSize = tRow.Find("td:eq(9)").Text().ToLowerInvariant();
                        var size = ReleaseInfo.GetBytes(humanSize);
                        Output("Size: " + humanSize + " (" + size + " bytes)");

                        // --> Date
                        var dateTimeOrig = tRow.Find("td:eq(5)").Text();
                        var dateTime = Regex.Replace(dateTimeOrig, @"<[^>]+>|&nbsp;", "").Trim();
                        var date = DateTime.ParseExact(dateTime, "HH:mm:ss dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToLocalTime();
                        Output("Released on: " + date);

                        // Torrent Details URL
                        var detailsLink = new Uri(TorrentDescriptionUrl.Replace("{id}", id.ToString()));
                        Output("Details: " + detailsLink.AbsoluteUri);

                        // Torrent Filename
                        var fileName = tRow.Find("td:eq(4) > a:eq(0)").Attr("href").Split('=').Last();
                        Output("Filename: " + detailsLink.AbsoluteUri);

                        // Torrent Comments URL
                        var commentsLink = new Uri(TorrentCommentUrl.Replace("{id}", id.ToString()));
                        Output("Comments Link: " + commentsLink.AbsoluteUri);

                        // Torrent Download URL
                        Uri downloadLink = new Uri(TorrentDownloadUrl.Replace("{id}", id.ToString()).Replace("{filename}", fileName));
                        Output("Download Link: " + downloadLink.AbsoluteUri);

                        // Building release infos
                        var release = new ReleaseInfo
                        {
                            Category = MapTrackerCatToNewznab(categoryName.ToString()),
                            Title = name,
                            Seeders = seeders,
                            Peers = seeders + leechers,
                            MinimumRatio = 1,
                            MinimumSeedTime = 172800,
                            PublishDate = date,
                            Size = size,
                            Files = files,
                            Grabs = completed,
                            Guid = detailsLink,
                            Comments = commentsLink,
                            Link = downloadLink
                        };

                        var genres = release.Description;
                        
                        if (tRow.Find("img[title=\"Gold 100% Free\"]").Length >= 1)
                            release.DownloadVolumeFactor = 0;
                        else
                            release.DownloadVolumeFactor = 1;

                        release.UploadVolumeFactor = 1;

                        releases.Add(release);
                    }
                }
                catch (Exception ex)
                {
                    OnParseError("Error, unable to parse result \n" + ex.StackTrace, ex);
                }
            }
            // Return found releases
            return releases;
        }

        /// <summary>
        /// Build query to process
        /// </summary>
        /// <param name="term">Term to search</param>
        /// <param name="query">Torznab Query for categories mapping</param>
        /// <param name="url">Search url for provider</param>
        /// <param name="page">Page number to request</param>
        /// <returns>URL to query for parsing and processing results</returns>
        private string BuildQuery(string term, TorznabQuery query, string url, int page = 0)
        {
            var parameters = new NameValueCollection();
            var categoriesList = MapTorznabCapsToTrackers(query);
            string searchterm = term;
            string searchFileandDesc = "0";

            // Building our tracker query
            parameters.Add("active", "0");

            // If search term provided
            if (!string.IsNullOrWhiteSpace(query.ImdbID))
            {
                searchFileandDesc = "1";
            }
            else if (!string.IsNullOrWhiteSpace(term))
            {
                searchterm = "search=" + WebUtilityHelpers.UrlEncode(term, Encoding.GetEncoding(28591));
            }
            else
            {
                // Showing all torrents (just for output function)
                searchterm = "search=";
                term = "all";
            }

            parameters.Add("options", searchFileandDesc);

            var CatQryStr = "";
            if (categoriesList.Count > 1) // the search page only allows searching in one category or all
            {
                CatQryStr = "category=0";
            } else if (categoriesList.Count == 1)
            {
                CatQryStr = categoriesList[0];
            }

            // Building our query
            url += "&" + searchterm + "&" + parameters.GetQueryString() + "&" + CatQryStr;

            Output("\nBuilt query for \"" + term + "\"... " + url);

            // Return our search url
            return url;
        }

        /// <summary>
        /// Switch Method for Querying
        /// </summary>
        /// <param name="request">URL created by Query Builder</param>
        /// <returns>Results from query</returns>
        private async Task<WebClientStringResult> QueryExec(string request)
        {
            WebClientStringResult results;

            // Switch in we are in DEV mode with Hard Drive Cache or not
            if (DevMode && CacheMode)
            {
                // Check Cache before querying and load previous results if available
                results = await QueryCache(request);
            }
            else
            {
                // Querying tracker directly
                results = await QueryTracker(request);
            }
            return results;
        }

        /// <summary>
        /// Get Torrents Page from Cache by Query Provided
        /// </summary>
        /// <param name="request">URL created by Query Builder</param>
        /// <returns>Results from query</returns>
        private async Task<WebClientStringResult> QueryCache(string request)
        {
            WebClientStringResult results;

            // Create Directory if not exist
            System.IO.Directory.CreateDirectory(Directory);

            // Clean Storage Provider Directory from outdated cached queries
            CleanCacheStorage();

            // Create fingerprint for request
            var file = Directory + request.GetHashCode() + ".json";

            // Checking modes states
            if (System.IO.File.Exists(file))
            {
                // File exist... loading it right now !
                Output("Loading results from hard drive cache ..." + request.GetHashCode() + ".json");
                results = JsonConvert.DeserializeObject<WebClientStringResult>(System.IO.File.ReadAllText(file));
            }
            else
            {
                // No cached file found, querying tracker directly
                results = await QueryTracker(request);

                // Cached file didn't exist for our query, writing it right now !
                Output("Writing results to hard drive cache ..." + request.GetHashCode() + ".json");
                System.IO.File.WriteAllText(file, JsonConvert.SerializeObject(results));
            }
            return results;
        }

        /// <summary>
        /// Get Torrents Page from Tracker by Query Provided
        /// </summary>
        /// <param name="request">URL created by Query Builder</param>
        /// <returns>Results from query</returns>
        private async Task<WebClientStringResult> QueryTracker(string request)
        {
            // Cache mode not enabled or cached file didn't exist for our query
            Output("\nQuerying tracker for results....");

            // Request our first page
            LatencyNow();
            var results = await RequestStringWithCookiesAndRetry(request, ConfigData.CookieHeader.Value, SearchUrl, _emulatedBrowserHeaders);

            // Return results from tracker
            return results;
        }

        /// <summary>
        /// Clean Hard Drive Cache Storage
        /// </summary>
        /// <param name="force">Force Provider Folder deletion</param>
        private void CleanCacheStorage(bool force = false)
        {
            // Check cleaning method
            if (force)
            {
                // Deleting Provider Storage folder and all files recursively
                Output("\nDeleting Provider Storage folder and all files recursively ...");

                // Check if directory exist
                if (System.IO.Directory.Exists(Directory))
                {
                    // Delete storage directory of provider
                    System.IO.Directory.Delete(Directory, true);
                    Output("-> Storage folder deleted successfully.");
                }
                else
                {
                    // No directory, so nothing to do
                    Output("-> No Storage folder found for this provider !");
                }
            }
            else
            {
                var i = 0;
                // Check if there is file older than ... and delete them
                Output("\nCleaning Provider Storage folder... in progress.");
                System.IO.Directory.GetFiles(Directory)
                .Select(f => new System.IO.FileInfo(f))
                .Where(f => f.LastAccessTime < DateTime.Now.AddMilliseconds(-Convert.ToInt32(ConfigData.HardDriveCacheKeepTime.Value)))
                .ToList()
                .ForEach(f =>
                {
                    Output("Deleting cached file << " + f.Name + " >> ... done.");
                    f.Delete();
                    i++;
                });

                // Inform on what was cleaned during process
                if (i > 0)
                {
                    Output("-> Deleted " + i + " cached files during cleaning.");
                }
                else
                {
                    Output("-> Nothing deleted during cleaning.");
                }
            }
        }

        /// <summary>
        /// Generate a random fake latency to avoid detection on tracker side
        /// </summary>
        private void LatencyNow()
        {
            // Need latency ?
            if (Latency)
            {
                var random = new Random(DateTime.Now.Millisecond);
                var waiting = random.Next(Convert.ToInt32(ConfigData.LatencyStart.Value),
                    Convert.ToInt32(ConfigData.LatencyEnd.Value));
                Output("\nLatency Faker => Sleeping for " + waiting + " ms...");

                // Sleep now...
                System.Threading.Thread.Sleep(waiting);
            }
            // Generate a random value in our range
        }

        /// <summary>
        /// Find torrent rows in search pages
        /// </summary>
        /// <returns>JQuery Object</returns>
        private CQ FindTorrentRows()
        {
            // Return all occurencis of torrents found
            //return _fDom["#content > table > tr"];
            return _fDom["#mcol > div > div.block-content > div > table > tbody > tr:not(:first) > td > table.lista > tbody >tr:not(:first)"];
        }

        /// <summary>
        /// Download torrent file from tracker
        /// </summary>
        /// <param name="link">URL string</param>
        /// <returns></returns>
        public override async Task<byte[]> Download(Uri link)
        {
            // Retrieving ID from link provided
            var id = ParseUtil.CoerceInt(Regex.Match(link.AbsoluteUri, @"\d+").Value);
            Output("Torrent Requested ID: " + id);

            // Building login form data
            var pairs = new Dictionary<string, string> {
                { "torrentid", id.ToString() },
                { "_", string.Empty } // ~~ Strange, blank param...
            };

            // Add emulated XHR request
            _emulatedBrowserHeaders.Add("X-Prototype-Version", "1.6.0.3");
            _emulatedBrowserHeaders.Add("X-Requested-With", "XMLHttpRequest");

            // Get torrent file now
            Output("Getting torrent file now....");
            var response = await base.Download(link);

            // Remove our XHR request header
            _emulatedBrowserHeaders.Remove("X-Prototype-Version");
            _emulatedBrowserHeaders.Remove("X-Requested-With");

            // Return content
            return response;
        }

        /// <summary>
        /// Output message for logging or developpment (console)
        /// </summary>
        /// <param name="message">Message to output</param>
        /// <param name="level">Level for Logger</param>
        private void Output(string message, string level = "debug")
        {
            // Check if we are in dev mode
            if (DevMode)
            {
                // Output message to console
                Console.WriteLine(message);
            }
            else
            {
                // Send message to logger with level
                switch (level)
                {
                    default:
                        goto case "debug";
                    case "debug":
                        // Only if Debug Level Enabled on Jackett
                        if (logger.IsDebugEnabled)
                        {
                            logger.Debug(message);
                        }
                        break;

                    case "info":
                        logger.Info(message);
                        break;

                    case "error":
                        logger.Error(message);
                        break;
                }
            }
        }

        /// <summary>
        /// Validate Config entered by user on Jackett
        /// </summary>
        private void ValidateConfig()
        {
            Output("\nValidating Settings ... \n");

            // Check Username Setting
            if (string.IsNullOrEmpty(ConfigData.Username.Value))
            {
                throw new ExceptionWithConfigData("You must provide a username for this tracker to login !", ConfigData);
            }
            else
            {
                Output("Validated Setting -- Username (auth) => " + ConfigData.Username.Value);
            }

            // Check Password Setting
            if (string.IsNullOrEmpty(ConfigData.Password.Value))
            {
                throw new ExceptionWithConfigData("You must provide a password with your username for this tracker to login !", ConfigData);
            }
            else
            {
                Output("Validated Setting -- Password (auth) => " + ConfigData.Password.Value);
            }

            // Check Max Page Setting
            if (!string.IsNullOrEmpty(ConfigData.Pages.Value))
            {
                try
                {
                    Output("Validated Setting -- Max Pages => " + Convert.ToInt32(ConfigData.Pages.Value));
                }
                catch (Exception)
                {
                    throw new ExceptionWithConfigData("Please enter a numeric maximum number of pages to crawl !", ConfigData);
                }
            }
            else
            {
                throw new ExceptionWithConfigData("Please enter a maximum number of pages to crawl !", ConfigData);
            }

            // Check Latency Setting
            if (ConfigData.Latency.Value)
            {
                Output("\nValidated Setting -- Latency Simulation enabled");

                // Check Latency Start Setting
                if (!string.IsNullOrEmpty(ConfigData.LatencyStart.Value))
                {
                    try
                    {
                        Output("Validated Setting -- Latency Start => " + Convert.ToInt32(ConfigData.LatencyStart.Value));
                    }
                    catch (Exception)
                    {
                        throw new ExceptionWithConfigData("Please enter a numeric latency start in ms !", ConfigData);
                    }
                }
                else
                {
                    throw new ExceptionWithConfigData("Latency Simulation enabled, Please enter a start latency !", ConfigData);
                }

                // Check Latency End Setting
                if (!string.IsNullOrEmpty(ConfigData.LatencyEnd.Value))
                {
                    try
                    {
                        Output("Validated Setting -- Latency End => " + Convert.ToInt32(ConfigData.LatencyEnd.Value));
                    }
                    catch (Exception)
                    {
                        throw new ExceptionWithConfigData("Please enter a numeric latency end in ms !", ConfigData);
                    }
                }
                else
                {
                    throw new ExceptionWithConfigData("Latency Simulation enabled, Please enter a end latency !", ConfigData);
                }
            }

            // Check Browser Setting
            if (ConfigData.Browser.Value)
            {
                Output("\nValidated Setting -- Browser Simulation enabled");

                // Check ACCEPT header Setting
                if (string.IsNullOrEmpty(ConfigData.HeaderAccept.Value))
                {
                    throw new ExceptionWithConfigData("Browser Simulation enabled, Please enter an ACCEPT header !", ConfigData);
                }
                else
                {
                    Output("Validated Setting -- ACCEPT (header) => " + ConfigData.HeaderAccept.Value);
                }

                // Check ACCEPT-LANG header Setting
                if (string.IsNullOrEmpty(ConfigData.HeaderAcceptLang.Value))
                {
                    throw new ExceptionWithConfigData("Browser Simulation enabled, Please enter an ACCEPT-LANG header !", ConfigData);
                }
                else
                {
                    Output("Validated Setting -- ACCEPT-LANG (header) => " + ConfigData.HeaderAcceptLang.Value);
                }

                // Check USER-AGENT header Setting
                if (string.IsNullOrEmpty(ConfigData.HeaderUserAgent.Value))
                {
                    throw new ExceptionWithConfigData("Browser Simulation enabled, Please enter an USER-AGENT header !", ConfigData);
                }
                else
                {
                    Output("Validated Setting -- USER-AGENT (header) => " + ConfigData.HeaderUserAgent.Value);
                }
            }
            else
            {
                // Browser simulation must be enabled (otherwhise, this provider will not work due to tracker's security)
                throw new ExceptionWithConfigData("Browser Simulation must be enabled for this provider to work, please enable it !", ConfigData);
            }

            // Check Dev Cache Settings
            if (ConfigData.HardDriveCache.Value)
            {
                Output("\nValidated Setting -- DEV Hard Drive Cache enabled");

                // Check if Dev Mode enabled !
                if (!ConfigData.DevMode.Value)
                {
                    throw new ExceptionWithConfigData("Hard Drive is enabled but not in DEV MODE, Please enable DEV MODE !", ConfigData);
                }

                // Check Cache Keep Time Setting
                if (!string.IsNullOrEmpty(ConfigData.HardDriveCacheKeepTime.Value))
                {
                    try
                    {
                        Output("Validated Setting -- Cache Keep Time (ms) => " + Convert.ToInt32(ConfigData.HardDriveCacheKeepTime.Value));
                    }
                    catch (Exception)
                    {
                        throw new ExceptionWithConfigData("Please enter a numeric hard drive keep time in ms !", ConfigData);
                    }
                }
                else
                {
                    throw new ExceptionWithConfigData("Hard Drive Cache enabled, Please enter a maximum keep time for cache !", ConfigData);
                }
            }
            else
            {
                // Delete cache if previously existed
                CleanCacheStorage(true);
            }
        }
    }
}
