using System;
using System.Drawing.Mac;
using System.Windows.Forms.Mac;
using AppKit;

namespace System.Windows.Forms
{
	public partial class Button : IMacNativeControl
	{
		NSButton button;

		// A Button subclass that paints itself (OnPaint overridden, such as Mission Planner's
		// themed MyButton) is drawn by its own code, as on Windows: an NSButton would ignore it.
		bool? paintsItself;
		bool PaintsItself
		{
			get
			{
				if (paintsItself == null)
				{
					var onPaint = GetType().GetMethod("OnPaint", Reflection.BindingFlags.Instance | Reflection.BindingFlags.NonPublic | Reflection.BindingFlags.Public,
						null, new[] { typeof(PaintEventArgs) }, null);
					// an override of Control.OnPaint (not a 'new' method) below Button
					var declaring = onPaint?.DeclaringType;
					paintsItself = declaring != null && onPaint.GetBaseDefinition().DeclaringType == typeof(Control)
						&& declaring != typeof(Button) && declaring != typeof(ButtonBase) && declaring != typeof(Control);
				}
				return paintsItself.Value;
			}
		}

		public NSView CreateView()
		{
			// null: a Windows Forms view (MonoView) that the control paints
			if (PaintsItself)
				return null;

			var i = Image;

			button = new NSButton();
			/*button.AttributedTitle = GetAttributedString(Text, '&', b.font, b.TextAlign);
			button.Alignment = TextAlign.ToNSTextAlignment();
			cell.Highlighted = b.ButtonState == ButtonState.Pushed;
			cell.Bezeled = false;
			cell.Bordered = true;
			cell.BezelStyle = NSBezelStyle.Rounded; // When Rounded is set, the button border gets its own dimensions (smaller than we want)
			cell.Enabled = b.Enabled;
			cell.isFocused = b.Focused;*/
			button.BezelStyle = NSBezelStyle.Rounded;
			button.AttributedTitle = System.Drawing.Mac.Extensions.GetAttributedString(Text, '&', Font, TextAlign);
			button.Alignment = TextAlign.ToNSTextAlignment();
			button.Activated += (sender, e) => PerformClick();
			button.Enabled = Enabled;
			button.Image = i == null ? null : i.ToNSImage();
			if (IsDefault)
				button.KeyEquivalent = "\r";

			return button;
		}

		public override string Text
		{
			get
			{
				return base.Text;
			}
			set
			{
				base.Text = value;
				if (button != null)
					button.AttributedTitle = System.Drawing.Mac.Extensions.GetAttributedString(value, '&', Font, TextAlign);
			}
		}

		public override Drawing.Font Font
		{
			get
			{
				return base.Font;
			}
			set
			{
				base.Font = value;
				if (button != null)
					button.AttributedTitle = System.Drawing.Mac.Extensions.GetAttributedString(Text, '&', value, TextAlign);
			}
		}

		public override Drawing.ContentAlignment TextAlign
		{
			get
			{
				return base.TextAlign;
			}
			set
			{
				base.TextAlign = value;
				if (button != null)
				{
					button.AttributedTitle = System.Drawing.Mac.Extensions.GetAttributedString(Text, '&', Font, value);
					button.Alignment = value.ToNSTextAlignment();
				}
			}
		}

		protected override void OnEnabledChanged(EventArgs e)
		{
			if (button != null)
				button.Enabled = Enabled;
			base.OnEnabledChanged(e);
		}

		internal override Drawing.Size GetPreferredSizeCore(Drawing.Size proposedSize)
		{
			if (this.AutoSize && NativeButton is NSButton button)
				return button.SizeThatFits(proposedSize.ToCGSize()).ToSDSize();

			return base.GetPreferredSizeCore(proposedSize);
		}

		internal virtual NSButton? NativeButton
		{
			get
			{
				if (button == null && !PaintsItself)
					CreateView();
				return button;
			}
		}

		internal protected override bool IsDefault
		{
			get
			{
				return base.IsDefault;
			}
			set
			{
				base.IsDefault = value;
				if (button != null)
					button.KeyEquivalent = "\r";
			}
		}

		internal override void OnImageChanged()
		{
			if (button != null)
			{
				var i = Image;
				button.Image = i == null ? null : i.ToNSImage();
			}
			base.OnImageChanged();
		}
	}
}
