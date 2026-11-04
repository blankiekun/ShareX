#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable disable

namespace ShareX.Tools
{
    public class VideoConverterOptions
    {
        public string InputFilePath { get; set; }
        public string OutputFolderPath { get; set; }
        public string OutputFileName { get; set; }

        public string OutputFilePath
        {
            get
            {
                string path = "";

                if (!string.IsNullOrEmpty(OutputFolderPath) && !string.IsNullOrEmpty(OutputFileName))
                {
                    path = Path.Combine(OutputFolderPath, OutputFileName);

                    if (!Path.HasExtension(OutputFileName))
                    {
                        string extension = GetFileExtension();
                        path = Path.ChangeExtension(path, extension);
                    }
                }

                return path;
            }
        }

        public ConverterVideoCodecs VideoCodec { get; set; } = ConverterVideoCodecs.x264;
        public int VideoQuality { get; set; } = 23;
        public bool VideoQualityUseBitrate { get; set; } = false;
        public int VideoQualityBitrate { get; set; } = 3000;

        public bool UseCustomArguments { get; set; } = false;
        public string CustomArguments { get; set; } = "";

        public bool AutoOpenFolder { get; set; } = true;

        public string GetFileExtension()
        {
            switch (VideoCodec)
            {
                default:
                case ConverterVideoCodecs.x264:
                case ConverterVideoCodecs.x265:
                case ConverterVideoCodecs.h264_nvenc:
                case ConverterVideoCodecs.hevc_nvenc:
                case ConverterVideoCodecs.h264_amf:
                case ConverterVideoCodecs.hevc_amf:
                case ConverterVideoCodecs.h264_qsv:
                case ConverterVideoCodecs.hevc_qsv:
                    return "mp4";
                case ConverterVideoCodecs.vp8:
                case ConverterVideoCodecs.vp9:
                    return "webm";
                case ConverterVideoCodecs.av1:
                    return "mkv";
                case ConverterVideoCodecs.xvid:
                    return "avi";
                case ConverterVideoCodecs.gif:
                    return "gif";
                case ConverterVideoCodecs.webp:
                    return "webp";
                case ConverterVideoCodecs.apng:
                    return "apng";
            }
        }
    }
}
