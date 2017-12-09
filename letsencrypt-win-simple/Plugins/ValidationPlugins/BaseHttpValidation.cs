﻿using ACMESharp;
using ACMESharp.ACME;
using LetsEncrypt.ACME.Simple.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace LetsEncrypt.ACME.Simple.Plugins.ValidationPlugins
{
    /// <summary>
    /// Base implementation for HTTP-01 validation plugins
    /// </summary>
    abstract class BaseHttpValidation : BaseValidation<HttpChallenge>
    {
        protected IInputService _input;
        protected ScheduledRenewal _renewal;
        private ProxyService _proxy;

        /// <summary>
        /// Where to find the template for the web.config that's copied to the webroot
        /// </summary>
        protected readonly string _templateWebConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web_config.xml");

        /// <summary>
        /// Character to seperate folders, different for FTP 
        /// </summary>
        protected virtual char PathSeparator => '\\';

        public BaseHttpValidation(ILogService log, IInputService input, ProxyService proxy, ScheduledRenewal renewal, string identifier):
            base(log, identifier)
        {
            _input = input;
            _proxy = proxy;
            _renewal = renewal;
        }

        /// <summary>
        /// Handle http challenge
        /// </summary>
        public override void PrepareChallenge()
        {
            Refresh();
            CreateAuthorizationFile();
            BeforeAuthorize();
            _log.Information("Answer should now be browsable at {answerUri}", _challenge.FileUrl);
            if (_renewal.Test && _renewal.New)
            {
                if (_input.PromptYesNo("Try in default browser?"))
                {
                    Process.Start(_challenge.FileUrl);
                    _input.Wait();
                }
            }
            if (_renewal.Warmup)
            {
                _log.Information("Waiting for site to warmup...");
                WarmupSite();
            }
        }

        /// <summary>
        /// Warm up the target site, giving the application a little
        /// time to start up before the validation request comes in.
        /// Mostly relevant to classic FileSystem validation
        /// </summary>
        /// <param name="uri"></param>
        private void WarmupSite()
        {
            var request = WebRequest.Create(new Uri(_challenge.FileUrl));
            request.Proxy = _proxy.GetWebProxy();
            try
            {
                using (var response = request.GetResponse()) { }
            }
            catch (Exception ex)
            {
                _log.Error("Error warming up site: {@ex}", ex);
            }
        }

        /// <summary>
        /// Should create any directory structure needed and write the file for authorization
        /// </summary>
        /// <param name="answerPath">where the answerFile should be located</param>
        /// <param name="fileContents">the contents of the file to write</param>
        private void CreateAuthorizationFile()
        {
            WriteFile(CombinePath(_renewal.Binding.WebRootPath, _challenge.FilePath), _challenge.FileContent);
        }

        /// <summary>
        /// Can be used to write out server specific configuration, to handle extensionless files etc.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="answerPath"></param>
        /// <param name="token"></param>
        protected virtual void BeforeAuthorize()
        {
            if (_renewal.Binding.IIS == true)
            {
                _log.Debug("Writing web.config");
                var destination = CombinePath(_renewal.Binding.WebRootPath, _challenge.FilePath.Replace(_challenge.Token, "web.config"));
                var content = GetWebConfig();
                WriteFile(destination, content);
            }
        }

        /// <summary>
        /// Get the template for the web.config
        /// </summary>
        /// <returns></returns>
        private string GetWebConfig()
        {
            return File.ReadAllText(_templateWebConfig);
        }

        /// <summary>
        /// Can be used to write out server specific configuration, to handle extensionless files etc.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="answerPath"></param>
        /// <param name="token"></param>
        protected virtual void BeforeDelete()
        {
            if (_renewal.Binding.IIS == true)
            {
                _log.Debug("Deleting web.config");
                DeleteFile(CombinePath(_renewal.Binding.WebRootPath, _challenge.FilePath.Replace(_challenge.Token, "web.config")));
            }
        }

        /// <summary>
        /// Should delete any authorizations
        /// </summary>
        /// <param name="answerPath">where the answerFile should be located</param>
        /// <param name="token">the token</param>
        /// <param name="webRootPath">the website root path</param>
        /// <param name="filePath">the file path for the authorization file</param>
        private void DeleteAuthorization()
        {
            try
            {
                _log.Debug("Deleting answer");
                var path = CombinePath(_renewal.Binding.WebRootPath, _challenge.FilePath);
                DeleteFile(path);
                if (Properties.Settings.Default.CleanupFolders)
                {
                    path = path.Replace($"{PathSeparator}{_challenge.Token}", "");
                    if (DeleteFolderIfEmpty(path))
                    {
                        var idx = path.LastIndexOf(PathSeparator);
                        if (idx >= 0)
                        {
                            path = path.Substring(0, path.LastIndexOf(PathSeparator));
                            DeleteFolderIfEmpty(path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("Error occured while deleting folder structure. Error: {@ex}", ex);
            }
        }

        /// <summary>
        /// Combine root path with relative path
        /// </summary>
        /// <param name="root"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        private string CombinePath(string root, string path)
        {
            if (root == null) { root = string.Empty; }
            var expandedRoot = Environment.ExpandEnvironmentVariables(root);
            var trim = new[] { '/', '\\' };
            return $"{expandedRoot.TrimEnd(trim)}{PathSeparator}{path.TrimStart(trim).Replace('/', PathSeparator)}";
        }

        /// <summary>
        /// Delete folder if it's empty
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool DeleteFolderIfEmpty(string path)
        {
            if (IsEmpty(path))
            {
                DeleteFolder(path);
                return true;
            }
            else
            {
                _log.Debug("Additional files or folders exist in {folder}, not deleting.", path);
                return false;
            }
        }

        /// <summary>
        /// Write file with content to a specific location
        /// </summary>
        /// <param name="root"></param>
        /// <param name="path"></param>
        /// <param name="content"></param>
        protected abstract void WriteFile(string path, string content);

        /// <summary>
        /// Delete file from specific location
        /// </summary>
        /// <param name="root"></param>
        /// <param name="path"></param>
        protected abstract void DeleteFile(string path);

        /// <summary>
        /// Check if folder is empty
        /// </summary>
        /// <param name="root"></param>
        /// <param name="path"></param>
        protected abstract bool IsEmpty(string path);

        /// <summary>
        /// Delete folder if not empty
        /// </summary>
        /// <param name="root"></param>
        /// <param name="path"></param>
        protected abstract void DeleteFolder(string path);

        /// <summary>
        /// Refresh
        /// </summary>
        /// <param name="scheduled"></param>
        /// <returns></returns>
        protected virtual void Refresh() { }

        /// <summary>
        /// Dispose
        /// </summary>
        public override void Dispose()
        {
            BeforeDelete();
            DeleteAuthorization();
        }
    }
}
