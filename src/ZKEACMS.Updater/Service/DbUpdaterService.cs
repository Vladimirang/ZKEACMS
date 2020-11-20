/*!
 * http://www.zkea.net/
 * Copyright 2020 ZKEASOFT
 * http://www.zkea.net/licenses
 */

using Easy.Extend;
using Easy.Mvc.Plugin;
using Easy.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using ZKEACMS.Options;
using ZKEACMS.Updater.Models;

namespace ZKEACMS.Updater.Service
{
    public class DbUpdaterService : IDbUpdaterService
    {
        private readonly IOptions<DBVersionOption> _dbVersionOption;
        private readonly IWebClient _webClient;
        private readonly CurrentDbContext _dbContext;
        private readonly DatabaseOption _databaseOption;
        private const int DBVersionRecord = 1;
        private readonly string _scriptFileName;
        public DbUpdaterService(DatabaseOption databaseOption, IOptions<DBVersionOption> dbVersionOption, IWebClient webClient)
        {
            _dbVersionOption = dbVersionOption;
            _webClient = webClient;
            _dbContext = new CurrentDbContext(databaseOption);
            _databaseOption = databaseOption;
            _scriptFileName = $"{_databaseOption.DbType}.sql";//MsSql.sql, MySql.sql, Sqlite.sql
        }

        public void UpdateDatabase()
        {
            ExcuteLocalScripts();

            UpgradeDbToLatest();
        }

        private void UpgradeDbToLatest()
        {
            var appVersion = Easy.Version.Parse(Version.VersionInfo);
            DBVersion dbVersion = GetDbVersion();
            if (dbVersion < appVersion)
            {
                Console.WriteLine("Updating database to version: {0}.", appVersion);
                IEnumerable<string> sqlScripts = ReadRemoteScripts(dbVersion, appVersion);
                bool isSuccess = ExecuteScripts(sqlScripts);
                if (isSuccess)
                {
                    dbVersion.Major = appVersion.Major;
                    dbVersion.Minor = appVersion.Minor;
                    dbVersion.Revision = appVersion.Revision;
                    dbVersion.Build = appVersion.Build;

                    if (dbVersion.ID == DBVersionRecord)
                    {
                        _dbContext.DBVersion.Update(dbVersion);
                    }
                    _dbContext.SaveChanges();
                    
                    DeleteAllCachedScripts();
                }
            }
        }

        private DBVersion GetDbVersion()
        {
            //After 3.3.6, change to return application version if get database version failed.
            try
            {
                return _dbContext.DBVersion.Find(DBVersionRecord) ?? new DBVersion();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Getting database version failed. {0}", ex.Message);
            }
            return new DBVersion();
        }

        private void ExcuteLocalScripts()
        {
            List<string> localScripts = ReadLocalScripts();
            bool isSuccess = ExecuteScripts(localScripts);
            if (isSuccess)
            {
                DeleteLocalScripts();
            }
        }
        private List<string> ReadLocalScripts()
        {
            List<string> sqlScripts = new List<string>();
            string[] scriptFiles = GetLocalScriptFiles();
            foreach (var item in scriptFiles)
            {
                Console.WriteLine("Reading script: {0}", item);
                sqlScripts.AddRange(ReadSql(item));
            }
            return sqlScripts;
        }

        private void DeleteLocalScripts()
        {
            foreach (var item in GetLocalScriptFiles())
            {
                try
                {
                    File.Delete(item);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Query executed successfully, but failed to delete the script!");
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private IEnumerable<string> ReadSql(string scriptFile)
        {
            FileInfo file = new FileInfo(scriptFile);
            StringBuilder stringBuilder = new StringBuilder();
            using (FileStream fileStream = file.OpenRead())
            {
                using (StreamReader reader = new StreamReader(fileStream))
                {
                    string line = null;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Equals("GO", StringComparison.OrdinalIgnoreCase))
                        {
                            if (stringBuilder.Length > 0)
                            {
                                yield return stringBuilder.ToString().Trim();
                            }
                            stringBuilder.Clear();
                        }
                        else
                        {
                            stringBuilder.AppendLine(line);
                        }
                    }
                }
            }
            if (stringBuilder.Length > 0)
            {
                yield return stringBuilder.ToString().Trim();
            }
        }
        private string[] GetLocalScriptFiles()
        {
            string path = PluginBase.GetPath<UpdaterPlug>();
            return Directory.GetFiles(Path.Combine(path, "DbScripts"), "*.sql");
        }

        private IEnumerable<string> ReadRemoteScripts(Easy.Version dbVersion, Easy.Version appVersion)
        {
            List<string> sqlScripts = new List<string>();
            ReleaseVersion releaseVersion = GetReleaseVersions();
            if (releaseVersion == null) return sqlScripts;

            foreach (var item in releaseVersion.Versions)
            {
                var version = Easy.Version.Parse(item);
                if (version > dbVersion && version <= appVersion)
                {
                    try
                    {
                        sqlScripts.AddRange(GetUpdateScripts(item));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Download script package for version {0} failed. {1}", item, ex.Message);
                        sqlScripts.Clear();
                        break;
                    }
                }
            }
            return sqlScripts;
        }

        private ReleaseVersion GetReleaseVersions()
        {
            try
            {
                string source = $"{_dbVersionOption.Value.Source}/index.json";
                Console.WriteLine("Getting release versions. {0}", source);
                return JsonSerializer.Deserialize<ReleaseVersion>(_webClient.DownloadString(source));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Get release versions failed. {0}", ex.Message);
            }
            return null;
        }
        private IEnumerable<string> GetUpdateScripts(string version)
        {
            List<string> sqlScripts = new List<string>();

            byte[] packageByte = GetUpdateScriptsFromLocalCache(version);
            if (packageByte == null)
            {
                packageByte = GetUpdateScriptsFromRemote(version);
            }
            using (MemoryStream memoryStream = new MemoryStream(packageByte))
            {
                using (ZipArchive archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (!entry.Name.Equals(_scriptFileName, StringComparison.OrdinalIgnoreCase)) continue;

                        using (StreamReader reader = new StreamReader(entry.Open()))
                        {
                            string script = reader.ReadToEnd();
                            sqlScripts.AddRange(AsScripts(script));
                        }
                        break;
                    }
                }
            }
            return sqlScripts;
        }
        private byte[] GetUpdateScriptsFromLocalCache(string version)
        {
            string file = Path.Combine(PluginBase.GetPath<UpdaterPlug>(), "DbScripts", $"package.{version}.zip");
            if (File.Exists(file))
            {
                return File.ReadAllBytes(file);
            }
            return null;
        }
        private byte[] GetUpdateScriptsFromRemote(string version)
        {
            string packageUrl = $"{_dbVersionOption.Value.Source}/Update/{version}/package.zip";
            Console.WriteLine("Getting update scripts for version {0}. {1}", version, packageUrl);
            byte[] packageByte = _webClient.DownloadData(packageUrl);
            try
            {
                string file = Path.Combine(PluginBase.GetPath<UpdaterPlug>(), "DbScripts", $"package.{version}.zip");
                File.WriteAllBytes(file, packageByte);
            }
            catch { };

            return packageByte;
        }
        private void DeleteAllCachedScripts()
        {
            string[] cachedFiles = Directory.GetFiles(Path.Combine(PluginBase.GetPath<UpdaterPlug>(), "DbScripts", $"package.*.zip"));
            foreach (string file in cachedFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
        }
        private IEnumerable<string> AsScripts(string script)
        {
            StringReader stringReader = new StringReader(script);
            StringBuilder scriptBuilder = new StringBuilder();
            string line;
            while ((line = stringReader.ReadLine()) != null)
            {
                if (line.Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    if (scriptBuilder.Length > 0)
                    {
                        yield return scriptBuilder.ToString().Trim();
                    }
                    scriptBuilder.Clear();
                }
                else
                {
                    scriptBuilder.AppendLine(line);
                }
            }
            yield return scriptBuilder.ToString().Trim();
        }

        private bool ExecuteScripts(IEnumerable<string> sqlScripts)
        {
            if (!sqlScripts.Any())
            {
                return true;
            }
            Console.WriteLine("Executing scripts...");
            DbConnection dbConnection = _dbContext.Database.GetDbConnection();
            if (dbConnection.State != ConnectionState.Open)
            {
                dbConnection.Open();
            }

            using (DbTransaction dbTransaction = dbConnection.BeginTransaction())
            {
                try
                {
                    foreach (var sql in sqlScripts)
                    {
                        if (sql.IsNullOrWhiteSpace()) continue;
                        using (var command = dbConnection.CreateCommand())
                        {
                            command.Transaction = dbTransaction;
                            command.CommandTimeout = 0;
                            command.CommandText = sql;
                            command.ExecuteNonQuery();
                        }
                    }


                    dbTransaction.Commit();

                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    dbTransaction.Rollback();
                }
                finally
                {
                    if (dbConnection.State == ConnectionState.Open)
                    {
                        dbConnection.Close();
                    }
                }

            }
            return false;
        }

        public void Dispose()
        {
            _dbContext.Dispose();
            _webClient.Dispose();
        }
    }
}
