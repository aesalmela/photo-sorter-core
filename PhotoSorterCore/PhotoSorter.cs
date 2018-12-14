using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using ImageMagick;
using System.IO;
using System.Globalization;

namespace PhotoSorterCore
{
    public class PhotoSorter
    {
        private readonly ShutterflyAuth _secrets;
        private readonly AppConfigs _configs;
        private static List<string> videoExts { get; set; }
        private readonly char dirDel = Path.DirectorySeparatorChar;
        private string sfAuthID = "";

        public PhotoSorter(IOptions<AppConfigs> configs, IOptions<ShutterflyAuth> secrets)
        {
            _secrets = secrets.Value;
            _configs = configs.Value;
            videoExts = _configs.KnownVideoExtensions.Split(',').ToList();
        }

        public void RunImport()
        {
            if (Directory.Exists(_configs.ImportDir))
            {
                if (!Directory.Exists(_configs.PicDestinationDir))
                {
                    Directory.CreateDirectory(_configs.PicDestinationDir);
                }

                if (!Directory.Exists(_configs.VideoDestinationDir))
                {
                    Directory.CreateDirectory(_configs.VideoDestinationDir);
                }

                //Initialize Shutterfly
                if (_configs.ShutterflyUpload)
                {
                    if (_secrets.SFAppID != "" && _secrets.SFSharedSecret != "" && _secrets.SFUsername != "" && _secrets.SFPassword != "")
                    {
                        string authenticationID = ShutterflyUpload.GetAuthenticationID(_secrets.SFUsername, _secrets.SFPassword, _secrets.SFAppID, _secrets.SFSharedSecret);
                        if (!authenticationID.StartsWith("Failed:"))
                        {
                            sfAuthID = authenticationID;
                        }
                    }
                }

                FileInfo[] Pictures = (from fi in new DirectoryInfo(_configs.ImportDir).GetFiles("*.*", SearchOption.AllDirectories)
                                       where !videoExts.Contains(fi.Extension.ToLower())
                                       select fi)
                                            .ToArray();

                FileInfo[] Videos = (from fi in new DirectoryInfo(_configs.ImportDir).GetFiles("*.*", SearchOption.AllDirectories)
                                     where videoExts.Contains(fi.Extension.ToLower())
                                     select fi)
                                            .ToArray();

                List<string> moveErrors = new List<string>();
                List<string> uploadErrors = new List<string>();

                foreach (FileInfo pictureFile in Pictures)
                {
                    ProcessPicture(pictureFile, ref moveErrors, ref uploadErrors);
                }

                foreach (FileInfo videoFile in Videos)
                {
                    processVideo(videoFile, ref moveErrors);
                }

                if (moveErrors.Count > 0 || uploadErrors.Count > 0)
                {
                    //sendEmail(moveErrors, uploadErrors);
                }

                CleanUp();
            }
            else
            {
                string error = "Import directory does not exist. Check your settings.";
            }
        }

        void CleanUp()
        {
            DirectoryInfo[] subDirs = (from di in new DirectoryInfo(_configs.ImportDir).GetDirectories("*.*", SearchOption.AllDirectories)
                                       where (Directory.EnumerateFileSystemEntries(di.FullName).Any())
                                       select di)
                                             .ToArray();

            foreach (DirectoryInfo dir in subDirs)
            {
                try
                {
                    if (File.Exists($"{dir.FullName}{dirDel}Thumbs.db"))
                    {
                        File.Delete($"{dir.FullName}{dirDel}Thumbs.db");
                    }
                    Directory.Delete(dir.FullName);
                }
                catch { }
            }
        }

        private void ProcessPicture(FileInfo pictureFile, ref List<string> moveErrors, ref List<string> uploadErrors)
        {
            if (pictureFile.Name == "Thumbs.db" || pictureFile.Extension == ".ini")
            {
                return;   //skip
            }
            var image = new MagickImage(pictureFile.FullName);
            string name = image.FileName;
            DateTime pictureDate = GetPictureDate(image);

            StringBuilder newFileName = new StringBuilder();
            newFileName.Append(_configs.FileNamePrefix);
            newFileName.Append(pictureDate.ToString("yyyyMMdd_HHmmss"));
            string ext = pictureFile.Extension.ToLower();
            string moveToPath = CreatePicDirStructure(pictureDate);

            AutoRotate(image);

            if (_configs.FileNameUseCameraModel)
            {
                string cameraModel = GetCameraModel(image);
                if (cameraModel != "")
                {
                    newFileName.Append("_");
                    newFileName.Append(cameraModel);
                }
            }

            newFileName.Append(_configs.FileNameSuffix);

            string result = "";
            //Attempt to move
            if (moveToPath != "Error")
            {
                try
                {
                    result = movePicture(pictureFile.FullName, moveToPath, newFileName.ToString(), ext);
                }
                catch
                {
                    moveErrors.Add(pictureFile.Name);
                    MoveToManualFolder(pictureFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
                }

                if (result == "Error")
                {
                    moveErrors.Add(pictureFile.Name);
                    MoveToManualFolder(pictureFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
                }
                else
                {
                    if (_configs.ShutterflyUpload)
                    {
                        string uploadResult = uploadPicture(pictureDate, moveToPath, newFileName.ToString(), ext);
                        if (uploadResult.StartsWith("Failed"))
                        {
                            uploadErrors.Add(pictureFile.Name);
                        }
                    }
                    else
                    {
                        uploadErrors.Add(pictureFile.Name);
                    }
                }

            }
            else
            {
                moveErrors.Add(pictureFile.Name);
                MoveToManualFolder(pictureFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
            }

            result = "Successfully moved file " + pictureFile.Name + " to " + moveToPath + " as " + newFileName.ToString() + ext;
        }

        private DateTime GetPictureDate(MagickImage image)
        {
            //Set Date
            DateTime? pictureDate = null;

            //Get Date Taken
            ExifProfile profile = image.GetExifProfile();
            if (!(profile is null))
            {
                ExifValue dateTakeExif = profile.GetValue(ExifTag.DateTimeDigitized);
                if (!(dateTakeExif is null))
                {
                    pictureDate = DateTime.ParseExact(dateTakeExif.Value.ToString().TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss", null);
                }
            }
            //Get File Creation Date
            if (pictureDate is null)
            {
                pictureDate = File.GetCreationTime(image.FileName);
            }
            //Default to Today
            if (pictureDate is null)
            {
                pictureDate = DateTime.Now;
            }

            return pictureDate.Value;
        }

        private void AutoRotate(MagickImage image)
        {
            ExifProfile profile = image.GetExifProfile();
            if (!(profile is null))
            {
                string orientation = profile.GetValue(ExifTag.Orientation)?.ToString() ?? "";
                if (!String.IsNullOrWhiteSpace(orientation))
                {
                    switch (orientation)
                    {
                        case "2":
                            image.Flop(); //x-axis
                            break;
                        case "3":
                            image.Rotate(180);
                            break;
                        case "4":
                            image.Rotate(180);
                            image.Flop(); //x-axis
                            break;
                        case "5":
                            image.Rotate(90);
                            image.Flop(); //x-axis
                            break;
                        case "6":
                            image.Rotate(90);
                            break;
                        case "7":
                            image.Rotate(270);
                            image.Flop(); //x-axis
                            break;
                        case "8":
                            image.Rotate(270);
                            break;
                        case "1":
                        case "1H":
                        default:
                            break;
                    }
                    ushort defaultOrient = 1;
                    profile.SetValue(ExifTag.Orientation, defaultOrient);
                    image.Write(image.FileName);
                }
            }
        }

        private void processVideo(FileInfo videoFile, ref List<string> moveErrors)
        {
            DateTime myDateTaken = videoFile.CreationTime;
            string ext = videoFile.Extension.ToLower();
            StringBuilder newFileName = new StringBuilder();
            newFileName.Append(_configs.FileNamePrefix);
            newFileName.Append(myDateTaken.ToString("yyyyMMdd_HHmmss"));
            newFileName.Append(_configs.FileNameSuffix);

            if (_configs.FileNameUseCameraModel)
            {
                var image = new MagickImage(videoFile.FullName);
                string cameraModel = GetCameraModel(image);
                if (cameraModel != "")
                {
                    newFileName.Append("_");
                    newFileName.Append(cameraModel);
                }
            }

            string moveToPath = CreateVidDirStructure(ref myDateTaken);


            //Attempt to move
            if (moveToPath != "Error")
            {
                try
                {
                    string result = MoveVideo(videoFile.FullName, moveToPath, newFileName.ToString(), ext);
                    if (result == "Error")
                    {
                        moveErrors.Add(videoFile.Name);
                        MoveToManualFolder(videoFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
                    }
                }
                catch
                {
                    moveErrors.Add(videoFile.Name);
                    MoveToManualFolder(videoFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
                }
            }
            else
            {
                moveErrors.Add(videoFile.Name);
                MoveToManualFolder(videoFile.FullName, _configs.ImportDir, newFileName.ToString(), ext);
            }
        }

        private string CreatePicDirStructure(DateTime dt)
        {
            string year = dt.Year.ToString();
            string monthNum = dt.Month.ToString().PadLeft(2, '0');
            string monAbbrv = dt.ToString("MMM");

            try
            {
                string yearPath = $"{_configs.PicDestinationDir}{dirDel}{year}";
                string monthPath = $"{yearPath}{dirDel}{monthNum}_{monAbbrv}";
                if (!Directory.Exists(yearPath))
                {
                    Directory.CreateDirectory(yearPath);
                }
                if (!Directory.Exists(monthPath))
                {
                    Directory.CreateDirectory(monthPath);
                }
            }
            catch
            {
                return "Error";
            }

            return _configs.PicDestinationDir + "\\" + year + "\\" + monthNum + "_" + monAbbrv;
        }

        private string CreateVidDirStructure(ref DateTime dt)
        {
            string year = dt.Year.ToString();
            string vidPath = $"{_configs.VideoDestinationDir}{dirDel}{year}";
            try
            {
                if (!Directory.Exists(vidPath))
                {
                    Directory.CreateDirectory(vidPath);
                }
            }
            catch
            {
                return "Error";
            }

            return vidPath;
        }

        private string MoveVideo(string fromFilePath, string toPath, string fileName, string ext)
        {
            string path = $"{toPath}{dirDel}{fileName}{ext}";
            if (!File.Exists(path))
            {
                File.Move(fromFilePath, path);
            }
            else
            {
                int i;
                for (i = 1; i < 10; i++)
                {
                    if (!File.Exists($"{toPath}{dirDel}{fileName}_{i}{ext}"))
                    {
                        File.Move(fromFilePath, $"{toPath}{dirDel}{fileName}_{i}{ext}");
                        break;
                    }
                }
                if (i >= 10)
                {
                    return "Error";
                }
            }
            return "Success";
        }

        private string GetCameraModel(MagickImage image)
        {
            string cameraModel = "", model = "", make = "";
            ExifProfile profile = image.GetExifProfile();
            if (!(profile is null))
            {
                model = profile.GetValue(ExifTag.Model)?.ToString() ?? "";
                make = profile.GetValue(ExifTag.Make)?.ToString() ?? "";

                if (!String.IsNullOrWhiteSpace(model))
                {
                    cameraModel = model.TrimEnd('\0').Trim();
                }
                else
                {
                    if (!String.IsNullOrWhiteSpace(make))
                    {
                        cameraModel = make.TrimEnd('\0').Trim();
                    }
                }
            }
            return cameraModel;
        }

        private void MoveToManualFolder(string fromFilePath, string moveToPath, string fileName, string ext)
        {
            moveToPath = $"{moveToPath}{dirDel}ManualMove";
            if (!Directory.Exists(moveToPath))
            {
                Directory.CreateDirectory(moveToPath);
            }

            string baseFileName = $"{moveToPath}{dirDel}{fileName}";

            if (!File.Exists($"{baseFileName}{ext}"))
            {
                File.Copy(fromFilePath, $"{baseFileName}{ext}");
            }
            else
            {
                int i;
                for (i = 1; i < 10; i++)
                {
                    if (!File.Exists($"{baseFileName}_{i}{ext}"))
                    {
                        File.Copy(fromFilePath, $"{baseFileName}_{i}{ext}");
                        break;
                    }
                }
                if (i >= 10)
                {
                    File.Copy(fromFilePath, $"{baseFileName}_{DateTime.Now.ToString("MMddyyyyThhmmssffftt")}{ext}");
                }
            }
        }

        private string movePicture(string fromFilePath, string toPath, string fileName, string ext)
        {
            if (!File.Exists(toPath + "\\" + fileName + ext))
            {
                File.Move(fromFilePath, toPath + "\\" + fileName + ext);
            }
            else
            {
                int i;
                for (i = 1; i < 10; i++)
                {
                    if (!File.Exists(toPath + "\\" + fileName + "_" + i + ext))
                    {
                        File.Move(fromFilePath, toPath + "\\" + fileName + "_" + i + ext);
                        break;
                    }
                }
                if (i >= 10)
                {
                    return "Error";
                }
            }

            return "Success";
        }

        private string uploadPicture(DateTime dateTaken, string newPath, string fileName, string ext)
        {
            DateTimeFormatInfo mfi = new DateTimeFormatInfo();
            string monthName = mfi.GetMonthName(dateTaken.Month).ToString();
            return ShutterflyUpload.Upload(sfAuthID, _secrets.SFAppID, monthName, dateTaken.Year.ToString(), newPath, fileName + ext);
        }
    }
}
