﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Services.Connectors.SimianGrid
{
    /// <summary>
    /// Connects to the SimianGrid asset service
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class SimianAssetServiceConnector : IAssetService, ISharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);
        private static string ZeroID = UUID.Zero.ToString();

        private string m_serverUrl = String.Empty;
        private IImprovedAssetCache m_cache;

        #region ISharedRegionModule

        public Type ReplaceableInterface { get { return null; } }
        public void RegionLoaded(Scene scene)
        {
            if (m_cache == null)
            {
                IImprovedAssetCache cache = scene.RequestModuleInterface<IImprovedAssetCache>();
                if (cache is ISharedRegionModule)
                    m_cache = cache;
            }
        }
        public void PostInitialise() { }
        public void Close() { }

        public SimianAssetServiceConnector() { }
        public string Name { get { return "SimianAssetServiceConnector"; } }
        public void AddRegion(Scene scene) { if (!String.IsNullOrEmpty(m_serverUrl)) { scene.RegisterModuleInterface<IAssetService>(this); } }
        public void RemoveRegion(Scene scene) { if (!String.IsNullOrEmpty(m_serverUrl)) { scene.UnregisterModuleInterface<IAssetService>(this); } }
        
        #endregion ISharedRegionModule

        public SimianAssetServiceConnector(IConfigSource source)
        {
            Initialise(source);
        }

        public void Initialise(IConfigSource source)
        {
            if (Simian.IsSimianEnabled(source, "AssetServices", this.Name))
            {
                IConfig gridConfig = source.Configs["AssetService"];
                if (gridConfig == null)
                {
                    m_log.Error("[SIMIAN ASSET CONNECTOR]: AssetService missing from OpenSim.ini");
                    throw new Exception("Asset connector init error");
                }

                string serviceUrl = gridConfig.GetString("AssetServerURI");
                if (String.IsNullOrEmpty(serviceUrl))
                {
                    m_log.Error("[SIMIAN ASSET CONNECTOR]: No AssetServerURI in section AssetService");
                    throw new Exception("Asset connector init error");
                }

                if (!serviceUrl.EndsWith("/") && !serviceUrl.EndsWith("="))
                    serviceUrl = serviceUrl + '/';

                m_serverUrl = serviceUrl;
            }
        }

        #region IAssetService

        public AssetBase Get(string id)
        {
            AssetBase asset = null;

            // Cache fetch
            if (m_cache != null)
            {
                asset = m_cache.Get(id);
                if (asset != null)
                    return asset;
            }

            Uri url;

            // Determine if id is an absolute URL or a grid-relative UUID
            if (!Uri.TryCreate(id, UriKind.Absolute, out url))
                url = new Uri(m_serverUrl + id);

            try
            {
                HttpWebRequest request = UntrustedHttpWebRequest.Create(url);

                using (WebResponse response = request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        string creatorID = response.Headers.GetOne("X-Asset-Creator-Id") ?? String.Empty;

                        // Create the asset object
                        asset = new AssetBase(id, String.Empty, SLUtil.ContentTypeToSLAssetType(response.ContentType), creatorID);

                        UUID assetID;
                        if (UUID.TryParse(id, out assetID))
                            asset.FullID = assetID;
                        
                        // Grab the asset data from the response stream
                        using (MemoryStream stream = new MemoryStream())
                        {
                            responseStream.CopyTo(stream, Int32.MaxValue);
                            asset.Data = stream.ToArray();
                        }
                    }
                }

                // Cache store
                if (m_cache != null && asset != null)
                    m_cache.Cache(asset);

                return asset;
            }
            catch (Exception ex)
            {
                m_log.Warn("[SIMIAN ASSET CONNECTOR]: Asset GET from " + url + " failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Get an asset's metadata
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public AssetMetadata GetMetadata(string id)
        {
            AssetMetadata metadata = null;

            // Cache fetch
            if (m_cache != null)
            {
                AssetBase asset = m_cache.Get(id);
                if (asset != null)
                    return asset.Metadata;
            }

            Uri url;

            // Determine if id is an absolute URL or a grid-relative UUID
            if (!Uri.TryCreate(id, UriKind.Absolute, out url))
                url = new Uri(m_serverUrl + id);

            try
            {
                HttpWebRequest request = UntrustedHttpWebRequest.Create(url);
                request.Method = "HEAD";

                using (WebResponse response = request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        // Create the metadata object
                        metadata = new AssetMetadata();
                        metadata.ContentType = response.ContentType;
                        metadata.ID = id;

                        UUID uuid;
                        if (UUID.TryParse(id, out uuid))
                            metadata.FullID = uuid;

                        string lastModifiedStr = response.Headers.Get("Last-Modified");
                        if (!String.IsNullOrEmpty(lastModifiedStr))
                        {
                            DateTime lastModified;
                            if (DateTime.TryParse(lastModifiedStr, out lastModified))
                                metadata.CreationDate = lastModified;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.Warn("[SIMIAN ASSET CONNECTOR]: Asset GET from " + url + " failed: " + ex.Message);
            }

            return metadata;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = Get(id);

            if (asset != null)
                return asset.Data;

            return null;
        }

        /// <summary>
        /// Get an asset asynchronously
        /// </summary>
        /// <param name="id">The asset id</param>
        /// <param name="sender">Represents the requester.  Passed back via the handler</param>
        /// <param name="handler">The handler to call back once the asset has been retrieved</param>
        /// <returns>True if the id was parseable, false otherwise</returns>
        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
            Util.FireAndForget(
                delegate(object o)
                {
                    AssetBase asset = Get(id);
                    handler(id, sender, asset);
                }
            );

            return true;
        }

        /// <summary>
        /// Creates a new asset
        /// </summary>
        /// Returns a random ID if none is passed into it
        /// <param name="asset"></param>
        /// <returns></returns>
        public string Store(AssetBase asset)
        {
            bool storedInCache = false;
            string errorMessage = null;

            // AssetID handling
            if (String.IsNullOrEmpty(asset.ID) || asset.ID == ZeroID)
            {
                asset.FullID = UUID.Random();
                asset.ID = asset.FullID.ToString();
            }

            // Cache handling
            if (m_cache != null)
            {
                m_cache.Cache(asset);
                storedInCache = true;
            }

            // Local asset handling
            if (asset.Local)
            {
                if (!storedInCache)
                {
                    m_log.Error("Cannot store local " + asset.Metadata.ContentType + " asset without an asset cache");
                    asset.ID = null;
                    asset.FullID = UUID.Zero;
                }

                return asset.ID;
            }

            // Distinguish public and private assets
            bool isPublic = true;
            switch ((AssetType)asset.Type)
            {
                case AssetType.CallingCard:
                case AssetType.Gesture:
                case AssetType.LSLBytecode:
                case AssetType.LSLText:
                    isPublic = false;
                    break;
            }

            // Make sure ContentType is set
            if (String.IsNullOrEmpty(asset.Metadata.ContentType))
                asset.Metadata.ContentType = SLUtil.SLAssetTypeToContentType(asset.Type);

            // Build the remote storage request
            List<MultipartForm.Element> postParameters = new List<MultipartForm.Element>()
            {
                new MultipartForm.Parameter("AssetID", asset.FullID.ToString()),
                new MultipartForm.Parameter("CreatorID", asset.Metadata.CreatorID),
                new MultipartForm.Parameter("Temporary", asset.Temporary ? "1" : "0"),
                new MultipartForm.Parameter("Public", isPublic ? "1" : "0"),
                new MultipartForm.File("Asset", asset.Name, asset.Metadata.ContentType, asset.Data)
            };

            // Make the remote storage request
            try
            {
                HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(m_serverUrl);
                
                HttpWebResponse response = MultipartForm.Post(request, postParameters);
                using (Stream responseStream = response.GetResponseStream())
                {
                    try
                    {
                        string responseStr = responseStream.GetStreamString();
                        OSD responseOSD = OSDParser.Deserialize(responseStr);
                        if (responseOSD.Type == OSDType.Map)
                        {
                            OSDMap responseMap = (OSDMap)responseOSD;
                            if (responseMap["Success"].AsBoolean())
                                return asset.ID;
                            else
                                errorMessage = "Upload failed: " + responseMap["Message"].AsString();
                        }
                        else
                        {
                            errorMessage = "Response format was invalid.";
                        }
                    }
                    catch
                    {
                        errorMessage = "Failed to parse the response.";
                    }
                }
            }
            catch (WebException ex)
            {
                errorMessage = ex.Message;
            }

            m_log.WarnFormat("[SIMIAN ASSET CONNECTOR]: Failed to store asset \"{0}\" ({1}, {2}): {3}",
                asset.Name, asset.ID, asset.Metadata.ContentType, errorMessage);
            return null;
        }

        /// <summary>
        /// Update an asset's content
        /// </summary>
        /// Attachments and bare scripts need this!!
        /// <param name="id"> </param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool UpdateContent(string id, byte[] data)
        {
            AssetBase asset = Get(id);

            if (asset == null)
            {
                m_log.Warn("[SIMIAN ASSET CONNECTOR]: Failed to fetch asset " + id + " for updating");
                return false;
            }

            asset.Data = data;

            string result = Store(asset);
            return !String.IsNullOrEmpty(result);
        }

        /// <summary>
        /// Delete an asset
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public bool Delete(string id)
        {
            if (m_cache != null)
                m_cache.Expire(id);

            string url = m_serverUrl + id;

            OSDMap response = WebUtil.ServiceRequest(url, "DELETE");
            if (response["Success"].AsBoolean())
                return true;
            else
                m_log.Warn("[SIMIAN ASSET CONNECTOR]: Failed to delete asset " + id + " from the asset service");

            return false;
        }

        #endregion IAssetService
    }
}
