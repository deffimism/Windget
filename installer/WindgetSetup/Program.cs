using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindgetSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupWindow());
        }
    }

    internal sealed class SetupWindow : Form
    {
        private readonly Label titleLabel;
        private readonly Label statusLabel;
        private readonly ProgressBar progressBar;
        private bool isInstalling;
        private string extractedMsiPath;

        public SetupWindow()
        {
            Text = "Windget Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 170);
            Font = new Font("Segoe UI", 9F);

            titleLabel = new Label
            {
                AutoSize = false,
                Text = "Windget 설치 중",
                Font = new Font("Segoe UI", 14F, FontStyle.Bold),
                Location = new Point(24, 22),
                Size = new Size(360, 32)
            };

            statusLabel = new Label
            {
                AutoSize = false,
                Text = "설치 파일을 준비하는 중입니다.",
                Location = new Point(26, 68),
                Size = new Size(360, 24)
            };

            progressBar = new ProgressBar
            {
                Location = new Point(26, 106),
                Size = new Size(368, 20),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 28
            };

            Controls.Add(titleLabel);
            Controls.Add(statusLabel);
            Controls.Add(progressBar);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await InstallAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (isInstalling)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        private async Task InstallAsync()
        {
            isInstalling = true;

            try
            {
                extractedMsiPath = ExtractEmbeddedMsi();
                statusLabel.Text = "Windget을 설치하는 중입니다. 잠시만 기다려 주세요.";

                int exitCode = await RunMsiAsync(extractedMsiPath);
                if (exitCode == 0 || exitCode == 3010)
                {
                    statusLabel.Text = exitCode == 3010
                        ? "설치가 완료되었습니다. Windows 재시작이 필요할 수 있습니다."
                        : "설치가 완료되었습니다.";
                    await Task.Delay(1400);
                    isInstalling = false;
                    Close();
                    return;
                }

                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.MarqueeAnimationSpeed = 0;
                statusLabel.Text = "설치에 실패했습니다. 오류 코드: " + exitCode;
                MessageBox.Show(this, statusLabel.Text, "Windget Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.MarqueeAnimationSpeed = 0;
                statusLabel.Text = "설치를 시작하지 못했습니다.";
                MessageBox.Show(this, ex.Message, "Windget Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                isInstalling = false;
                TryDeleteExtractedMsi();
            }
        }

        private static Task<int> RunMsiAsync(string msiPath)
        {
            return Task.Run(
                delegate
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = "msiexec.exe",
                            Arguments = "/i \"" + msiPath + "\" /qn /norestart",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        process.Start();
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                });
        }

        private static string ExtractEmbeddedMsi()
        {
            Assembly assembly = typeof(SetupWindow).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream("WindgetInstaller.msi"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("설치 패키지를 찾을 수 없습니다.");
                }

                string directory = Path.Combine(Path.GetTempPath(), "WindgetSetup", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "Windget-v0.2.1-win-x64.msi");
                using (FileStream file = File.Create(path))
                {
                    stream.CopyTo(file);
                }

                return path;
            }
        }

        private void TryDeleteExtractedMsi()
        {
            if (string.IsNullOrWhiteSpace(extractedMsiPath))
            {
                return;
            }

            try
            {
                DirectoryInfo directory = Directory.GetParent(extractedMsiPath);
                if (directory != null && directory.Exists)
                {
                    directory.Delete(true);
                }
            }
            catch
            {
                // Temporary installer cleanup is best-effort.
            }
        }
    }
}
