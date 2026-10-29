using System.Drawing;
using System.Windows.Forms;
namespace VortexPass.Updater;
internal sealed class UpdateWindow : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready=new(false);
    private readonly TaskCompletionSource closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Form? form;private Label? detail;private Label? heading;private Button? close;private bool finished;
    public UpdateWindow(bool light,bool seasonal)
    {
        thread=new Thread(()=>
        {
            var bg=light?Color.FromArgb(255,248,241):Color.FromArgb(24,20,28);
            var fg=light?Color.FromArgb(52,34,55):Color.FromArgb(246,237,244);
            var accent=seasonal?Color.FromArgb(235,137,65):Color.FromArgb(55,133,250);
            form=new Form{Text="Обновление VortexPass",ClientSize=new Size(520,330),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=bg,ForeColor=fg,Font=new Font("Segoe UI",10),AutoScaleMode=AutoScaleMode.Dpi};
            var logoPath=Path.Combine(AppContext.BaseDirectory,"Assets",seasonal?"Halloween-logo.png":"VortexPass-logo.png");
            if(File.Exists(logoPath)){var logo=new PictureBox{Left=30,Top=24,Width=64,Height=64,SizeMode=PictureBoxSizeMode.Zoom,Image=Image.FromFile(logoPath)};form.Controls.Add(logo);form.Disposed+=(_,_)=>logo.Image?.Dispose();}
            heading=new Label{Left=112,Top=38,Width=370,Height=38,Text="Обновляем VortexPass",Font=new Font("Segoe UI",19,FontStyle.Bold)};
            detail=new Label{Left=30,Top=117,Width=455,Height=115,Text="Подготавливаем обновление…",AutoEllipsis=true};
            var progress=new ProgressBar{Left=30,Top=244,Width=455,Height=5,Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=30};
            close=new Button{Left=330,Top=274,Width=155,Height=35,Text="Закрыть",Enabled=false,Visible=false,FlatStyle=FlatStyle.Flat,BackColor=accent,ForeColor=Color.Black};close.FlatAppearance.BorderSize=0;
            close.Click+=(_,_)=>form.Close();form.FormClosing+=(_,e)=>{if(!finished)e.Cancel=true;};form.FormClosed+=(_,_)=>closed.TrySetResult();
            form.Controls.AddRange([heading,detail,progress,close]);form.Shown+=(_,_)=>ready.Set();Application.Run(form);form.Dispose();
        });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();ready.Wait(TimeSpan.FromSeconds(3));
    }
    public void Stage(string text){if(form is {IsHandleCreated:true,IsDisposed:false})form.BeginInvoke((Action)(()=>{if(detail is not null)detail.Text=text;}));}
    public Task SavePreview(string path)
    {
        var saved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(form is not {IsHandleCreated:true,IsDisposed:false})throw new InvalidOperationException("Preview window unavailable");
        form.BeginInvoke((Action)(()=>{try{using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);saved.SetResult();}catch(Exception ex){saved.SetException(ex);}}));return saved.Task;
    }
    public Task Failure(string text){if(form is {IsHandleCreated:true,IsDisposed:false})form.BeginInvoke((Action)(()=>{finished=true;heading!.Text="Не удалось обновить";detail!.Text=text;close!.Enabled=true;close.Visible=true;foreach(var bar in form.Controls.OfType<ProgressBar>())bar.Visible=false;}));else closed.TrySetResult();return closed.Task;}
    public void Dispose(){if(form is {IsHandleCreated:true,IsDisposed:false})form.BeginInvoke((Action)(()=>{finished=true;form.Close();}));}
}

