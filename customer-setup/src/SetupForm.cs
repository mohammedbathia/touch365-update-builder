using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Touch365Support
{
    public sealed class SetupResult
    {
        public bool Success;
        public string Message;
        public string ExecutablePath;
    }

    public class SetupForm : Form
    {
        private static readonly Color Navy = Color.FromArgb(20, 39, 66);
        private static readonly Color Teal = Color.FromArgb(0, 119, 125);
        private static readonly Color Ink = Color.FromArgb(36, 50, 67);
        private readonly bool preview;
        private readonly Func<Action<string>, Task<SetupResult>> action;
        private readonly Label status;
        private readonly Label detail;
        private readonly ProgressBar progress;
        private readonly Button primary;
        private readonly Button close;
        private bool running;
        private SetupResult result;

        public SetupForm(bool preview, Func<Action<string>, Task<SetupResult>> action)
        {
            if (action == null && !preview) throw new ArgumentNullException("action");
            this.preview = preview;
            this.action = action;

            Text = "Touch365 Remote Support - Setup " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Ink;
            BackColor = Color.White;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(580, 460);
            MinimumSize = new Size(596, 499);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var header = new TableLayoutPanel();
            header.AutoSize = true;
            header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            header.Dock = DockStyle.Fill;
            header.BackColor = Navy;
            header.Padding = new Padding(28, 17, 28, 14);
            header.Margin = Padding.Empty;
            header.ColumnCount = 1;
            header.RowCount = 2;
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title = MakeLabel("Touch365", new Font("Segoe UI", 21F, FontStyle.Bold), Color.White);
            title.Margin = Padding.Empty;
            header.Controls.Add(title, 0, 0);
            var subtitle = MakeLabel("Remote Support", Font, Color.FromArgb(205, 229, 236));
            subtitle.Margin = new Padding(1, 1, 0, 0);
            header.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel();
            body.AutoScroll = true;
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(28, 20, 28, 8);
            body.Margin = Padding.Empty;
            body.ColumnCount = 1;
            body.RowCount = 5;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var purpose = MakeLabel("Sets up customer-approved support with keyboard, clipboard and file sharing.", Font, Ink);
            purpose.Margin = new Padding(0, 0, 0, 18);
            body.Controls.Add(purpose, 0, 0);
            status = MakeLabel("Preparing your support setup...", new Font("Segoe UI", 11F, FontStyle.Bold), Teal);
            status.AccessibleName = "Setup status";
            status.Margin = new Padding(0, 0, 0, 8);
            body.Controls.Add(status, 0, 1);
            progress = new ProgressBar();
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 0, 0, 10);
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 25;
            progress.AccessibleName = "Setup progress";
            body.Controls.Add(progress, 0, 2);
            detail = MakeLabel("Windows may ask you to approve the first setup.", Font, Color.FromArgb(87, 101, 118));
            detail.Margin = new Padding(0, 9, 0, 0);
            detail.AccessibleName = "Setup instructions";
            body.Controls.Add(detail, 0, 3);
            root.Controls.Add(body, 0, 1);

            var footer = new TableLayoutPanel();
            footer.AutoSize = true;
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.FromArgb(244, 247, 250);
            footer.Padding = new Padding(28, 14, 28, 14);
            footer.Margin = Padding.Empty;
            footer.ColumnCount = 3;
            footer.RowCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var consent = MakeLabel("You approve every connection.", new Font("Segoe UI", 9F), Color.FromArgb(87, 101, 118));
            consent.Anchor = AnchorStyles.Left;
            consent.Margin = new Padding(0, 0, 14, 0);
            footer.Controls.Add(consent, 0, 0);
            primary = MakeButton("Setting up...", true);
            primary.Enabled = false;
            primary.Click += PrimaryClick;
            footer.Controls.Add(primary, 1, 0);
            close = MakeButton("Close", false);
            close.Enabled = preview;
            close.Margin = new Padding(10, 0, 0, 0);
            close.Click += delegate { Close(); };
            footer.Controls.Add(close, 2, 0);
            root.Controls.Add(footer, 0, 2);

            Shown += async delegate
            {
                if (!this.preview) await RunSetupAsync();
            };
        }

        private static Label MakeLabel(string text, Font font, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;
            label.UseMnemonic = false;
            label.Margin = Padding.Empty;
            return label;
        }

        private static Button MakeButton(string text, bool accent)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(accent ? 136 : 76, 36);
            button.Padding = new Padding(10, 4, 10, 4);
            button.Margin = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = accent ? Teal : Color.FromArgb(199, 209, 220);
            button.BackColor = accent ? Teal : Color.White;
            button.ForeColor = accent ? Color.White : Ink;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void ReportStatus(string message)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(ReportStatus), message); }
                catch (InvalidOperationException) { }
                return;
            }
            if (running && !String.IsNullOrWhiteSpace(message)) status.Text = message;
        }

        private async Task RunSetupAsync()
        {
            if (running || preview) return;
            running = true;
            result = null;
            primary.Enabled = false;
            primary.Text = "Setting up...";
            close.Enabled = false;
            status.ForeColor = Teal;
            status.Text = "Preparing your support setup...";
            detail.Text = "Windows may ask you to approve the first setup.";
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 25;
            try
            {
                result = await action(ReportStatus);
                if (result == null) throw new InvalidOperationException("Setup returned no result.");
            }
            catch (Exception)
            {
                result = new SetupResult
                {
                    Success = false,
                    Message = "Setup could not finish. Please try again. If it happens again, contact Touch365."
                };
            }
            finally
            {
                running = false;
                close.Enabled = true;
                progress.MarqueeAnimationSpeed = 0;
                progress.Style = ProgressBarStyle.Continuous;
            }

            if (IsDisposed || Disposing) return;
            if (result.Success)
            {
                status.Text = "Setup complete";
                detail.Text = "Setup complete. Wait for Ready in RustDesk, then share your ID with Touch365.";
                if (!String.IsNullOrWhiteSpace(result.Message)) detail.Text += "\r\n" + result.Message;
                progress.Value = 100;
                primary.Text = "Open RustDesk";
                primary.Enabled = !String.IsNullOrWhiteSpace(result.ExecutablePath);
                if (primary.Enabled) OpenRustDesk();
            }
            else
            {
                status.Text = "Setup needs attention";
                status.ForeColor = Color.FromArgb(154, 75, 33);
                detail.Text = String.IsNullOrWhiteSpace(result.Message)
                    ? "Setup could not finish. Please try again or contact Touch365."
                    : result.Message;
                progress.Value = 0;
                primary.Text = "Retry setup";
                primary.Enabled = true;
            }
        }

        private async void PrimaryClick(object sender, EventArgs e)
        {
            if (preview || running) return;
            if (result != null && result.Success) OpenRustDesk();
            else await RunSetupAsync();
        }

        private void OpenRustDesk()
        {
            if (preview || result == null || !result.Success || String.IsNullOrWhiteSpace(result.ExecutablePath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(result.ExecutablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(result.ExecutablePath)
                });
            }
            catch (Exception)
            {
                detail.Text = "Setup is complete, but RustDesk could not open. Click Open RustDesk to try again, or open RustDesk from the Start menu.";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                status.Text = "Setup is still running. Please wait until it finishes.";
            }
            base.OnFormClosing(e);
        }
    }
}
