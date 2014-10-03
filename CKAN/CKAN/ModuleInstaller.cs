using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using System.Text.RegularExpressions;

namespace CKAN
{
	public class ModuleInstaller
	{
		RegistryManager registry_manager = RegistryManager.Instance();

		public ModuleInstaller ()
		{
		}

		/// <summary>
		/// Download the given mod. Returns the filename it was saved to.
		///
		/// If no filename is provided, the standard_name() will be used.
		///
		/// </summary>
		/// <param name="filename">Filename.</param>
		public string download (Module module, string filename = null)
		{

			// Generate a temporary file if none is provided.
			if (filename == null) {
				filename = module.standard_name ();
			}

			Console.WriteLine ("    * Downloading " + filename + "...");

			string fullPath = Path.Combine (KSP.downloadCacheDir(), filename);

			WebClient agent = new WebClient ();
			agent.DownloadFile (module.download, fullPath);

			return fullPath;
		}

		public string cachedOrDownload(Module module, string filename = null) {
			if (filename == null) {
				filename = module.standard_name ();
			}

			string fullPath = cachePath (filename);

			if (File.Exists (fullPath)) {
				Console.WriteLine ("    * Using {0} (cached)", filename);
				return fullPath;
			}

			return download (module, filename);
		}

		public string cachePath(string file) {
			return Path.Combine (KSP.downloadCacheDir (), file);
		}

		/// <summary>
		/// Install our mod from the filename supplied.
		/// If no file is supplied, we will fetch() it first.
		/// </summary>

		public void install (Module module, string filename = null)
		{

			Console.WriteLine (module.identifier + ":\n");

			string version = registry_manager.registry.installedVersion (module.identifier);

			if (version != null) {
				// TODO: Check if we can upgrade!
				Console.WriteLine("    {0} {1} already installed, skipped", module.identifier, version);
				return;
			}

			// Check our dependencies.

			if (module.requires != null) {
				foreach (dynamic depends in module.requires) {
					string name = depends.name;
					string ver = registry_manager.registry.installedVersion (name);
					// TODO: Compare versions.

					if (ver == null) {

						// Oh, it's not installed! Let's see if we can find it.

						// TODO: A big store of all our known CKAN data, so we can go
						// find our module.

						// If we can't find it, cry and moan.
						Console.WriteLine ("Requirement {0} not found", depends.name);
						throw new ModuleNotFoundException (name, depends.version);
					}
				}
			}

			// Fetch our file if we don't already have it.
			if (filename == null) {
				filename = cachedOrDownload (module);
			}

			// We'll need our registry to record which files we've installed.
			Registry registry = registry_manager.registry;

			// And a list of files to record them to.
			Dictionary<string, InstalledModuleFile> module_files = new Dictionary<string, InstalledModuleFile> ();

			// Open our zip file for processing
			ZipFile zipfile = new ZipFile (File.OpenRead (filename));

			// Walk through our install instructions.
			foreach (dynamic stanza in module.install) {
				install_component (stanza, zipfile, module_files);
			}

			// Register our files.
			registry.register_module (new InstalledModule (module_files, module, DateTime.Now));

			// Handle bundled mods, if we have them.
			if (module.bundles != null) {

				foreach (dynamic stanza in module.bundles) {

					// TODO: Check versions, so we don't double install.

					Dictionary<string, InstalledModuleFile> installed_files = new Dictionary<string, InstalledModuleFile> ();

					install_component (stanza, zipfile, installed_files);

					// TODO: Make it possible to build modules from bundled stanzas
					Module bundled = new Module ();
					bundled.identifier = stanza.identifier;
					bundled.version    = stanza.version;
					bundled.license    = stanza.license;

					registry.register_module (new InstalledModule (installed_files, bundled, DateTime.Now));

				}
			}

			// Done! Save our registry changes!
			registry_manager.save();

			return;

		}

		string sha1_sum (string path)
		{
			SHA1 hasher = new SHA1CryptoServiceProvider();

			try {
				return BitConverter.ToString(hasher.ComputeHash (File.OpenRead (path)));
			}
			catch {
				return null;
			};
		}

		void install_component (dynamic stanza, ZipFile zipfile, Dictionary<string, InstalledModuleFile> module_files)
		{
			string fileToInstall = stanza.file;

			Console.WriteLine ("    * Installing " + fileToInstall);

			string installDir;
			bool makeDirs;

			if (stanza.install_to == "GameData") {
				installDir = KSP.gameData ();
				makeDirs = true;
			} else if (stanza.install_to == "Ships") {
				installDir = KSP.ships ();
				makeDirs = false; // Don't allow directory creation in ships directory
			} else {
				// Is this the best exception to use here??
				throw new BadCommandException ("Unknown install location: " + stanza.install_to);
			}

			// Console.WriteLine("InstallDir is "+installDir);

			// Is there a better way to extract a tree?
			string filter = "^" + stanza.file + "(/|$)";

			// O(N^2) solution, as we're walking the zipfile for each stanza.
			// Surely there's a better way, although this is fast enough we may not care.

			foreach (ZipEntry entry in zipfile) {

				// Skip things we don't want.
				if (! Regex.IsMatch (entry.Name, filter)) {
					continue;
				}

				// SKIP the file if it's a .CKAN file, these should never be copied to GameData.
				if (Regex.IsMatch (entry.Name, ".CKAN", RegexOptions.IgnoreCase)) {
					continue;
				}

				// Get the full name of the file.
				string outputName = entry.Name;

				// Strip off everything up to GameData/Ships
				// TODO: There's got to be a nicer way of doing path resolution.
				outputName = Regex.Replace (outputName, @"^/?(.*(GameData|Ships)/)?", "");


				// Aww hell yes, let's write this file out!

				string fullPath = Path.Combine (installDir, outputName);
				// Console.WriteLine (fullPath);

				copyZipEntry (zipfile, entry, fullPath, makeDirs);

				module_files.Add (Path.Combine((string) stanza.install_to, outputName), new InstalledModuleFile {
					sha1_sum = sha1_sum (fullPath),
				});
			}

			return;
		}

		void copyZipEntry (ZipFile zipfile, ZipEntry entry, string fullPath, bool makeDirs)
		{

			if (entry.IsDirectory) {

				// Skip if we're not making directories for this install.
				if (! makeDirs) {
					return;
				}

				// Console.WriteLine ("Making directory " + fullPath);
				Directory.CreateDirectory (fullPath);
			} else {
				// Console.WriteLine ("Writing file " + fullPath);

				// It's a file! Prepare the streams
				Stream zipStream = zipfile.GetInputStream (entry);
				FileStream output = File.Create (fullPath);

				// Copy
				zipStream.CopyTo (output);

				// Tidy up.
				zipStream.Close ();
				output.Close ();
			}

			return;
		}

		public void uninstall(string modName) {
			// Walk our registry to find all files for this mod.

			Dictionary<string, InstalledModuleFile> files = registry_manager.registry.installed_modules [modName].installed_files;

			// TODO: Finish!
		}

	}


	class ModuleNotFoundException : Exception {
		public string module;
		public string version;

		// TODO: Is there a way to set the stringify version of this?
		public ModuleNotFoundException (string mod, string ver) {
			module = mod;
			version = ver;
		}
	}
}