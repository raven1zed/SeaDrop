using System;
using System.IO;

namespace SeaDropWindows.SeaDrop.transfer
{
    /// <summary>
    /// FileHelper — file read/write, path validation.
    /// Provides safe file system operations for SeaDrop.
    /// </summary>
    public class FileHelper
    {
        /// <summary>
        /// Validates that a file path is safe and accessible.
        /// </summary>
        /// <param name="filePath">The file path to validate</param>
        /// <returns>True if the file path is valid and accessible</returns>
        public bool IsValidFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                var fullPath = Path.GetFullPath(filePath);
                return File.Exists(fullPath) &&
                       !IsSystemDirectory(fullPath) &&
                       IsAllowedLocation(fullPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads a file's contents as a byte array.
        /// </summary>
        /// <param name="filePath">The file path to read</param>
        /// <returns>The file contents as a byte array, or null if failed</returns>
        public byte[]? ReadFileBytes(string filePath)
        {
            if (!IsValidFilePath(filePath))
                return null;

            try
            {
                return File.ReadAllBytes(filePath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes a byte array to a file.
        /// </summary>
        /// <param name="filePath">The file path to write to</param>
        /// <param name="data">The data to write</param>
        /// <returns>True if successful</returns>
        public bool WriteFileBytes(string filePath, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(filePath) || data == null)
                return false;

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(filePath, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a path is in a system directory that should be avoided.
        /// </summary>
        private bool IsSystemDirectory(string path)
        {
            var systemPaths = new[]
            {
                Path.GetPathRoot(Environment.SystemDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System)
            };

            foreach (var sysPath in systemPaths)
            {
                if (!string.IsNullOrEmpty(sysPath) &&
                    path.StartsWith(sysPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a path is in an allowed user location.
        /// In a more restrictive implementation, this could limit to specific folders.
        /// </summary>
        private bool IsAllowedLocation(string path)
        {
            // For SeaDrop, we allow access to user folders and common locations
            // but restrict system directories
            return true;
        }

        /// <summary>
        /// Gets a safe temporary file path in the user's temp directory.
        /// </summary>
        /// <param name="extension">File extension (including dot, or empty)</param>
        /// <returns>A temporary file path</returns>
        public string GetTempFilePath(string extension = "")
        {
            var tempFile = Path.GetTempFileName();
            if (!string.IsNullOrEmpty(extension))
            {
                var newName = Path.ChangeExtension(tempFile, extension);
                File.Move(tempFile, newName);
                return newName;
            }
            return tempFile;
        }
    }
}