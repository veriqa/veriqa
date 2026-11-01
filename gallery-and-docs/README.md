# Gallery

What a person sees when your application uses Veriqa. The frames are screenshots of real runs of the
demo at [veriqa.app](https://veriqa.app). In the confirmation examples, the application window with the
QR code is a mock-up: in a real integration, your application draws that window.

The email examples come from a test deployment, where the email wording is set to name the
application.

The steps depend on the channel. In WhatsApp, the person first sends a ready-made message. With Google,
they pick an account and answer on a confirmation page instead of in a chat. By email, they send a ready-made
email and answer on a confirmation page.

Back to the [README](../README.md). Documentation: [docs.md](./docs.md). Installation: [INSTALL.md](./INSTALL.md).

## How the examples fit into your application

Every sign-in below is the middle block of this scheme. It starts in your app and returns to it by an
ordinary OpenID Connect redirect.

<p align="center">
  <img src="./media/readme/en-flow-sign-in.webp" width="800" alt="Signing in through Veriqa: your app sends the browser to the sign-in page by an OpenID Connect redirect, the person confirms in a channel, and Veriqa redirects back so your app can exchange the code for tokens." /><br />
</p>

Every action confirmation below runs between your server and Veriqa, with no redirect.

<p align="center">
  <img src="./media/readme/en-flow-confirm.webp" width="800" alt="Confirming an action through Veriqa: your server creates the transaction, the person scans the QR code your app shows and confirms in a channel, and your server reads the answer and performs the action." /><br />
</p>

## Telegram

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-telegram-cross-device.webp" width="800" alt="Signing in on a computer with Telegram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Telegram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A QR code, the phone camera, one tap in Telegram.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-telegram-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Telegram&quot;, confirms in the Telegram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A channel button, one tap in Telegram.</sub>
</p>

### Confirming an action

<p align="center">
  <img src="./media/readme/en-telegram-confirm.webp" width="800" alt="Confirming an action: the app shows a QR code for deleting a project, the phone camera scans it, the user gets the request in Telegram, and the decision goes back to the app." /><br />
  <sub>The app shows a QR code, the phone camera scans it, the request arrives in Telegram, the answer goes back to the app.</sub>
</p>

### Signing in with extra details

<p align="center">
  <img src="./media/readme/en-telegram-progressive.webp" width="270" alt="Sign-in with extra details: after confirming in Telegram, the user shares a phone number, fills in the missing fields of a short form, confirms the email with one ready-made message, and the app receives the verified claims." /><br />
  <sub>The same sign-in collects what the messaging app doesn't provide: a phone number, answers to a short form, a confirmed email address.</sub>
</p>

The values in the last frame of this animation are test data from a real ID token issued during the run.

### Claims

<p align="center">
  <img src="./media/readme/en-telegram-claims.webp" width="720" alt="Claims your app can get from signing in with Telegram: sub, amr, channel_type, channel_user_id, name, given_name, family_name, preferred_username, locale, picture, phone_number, phone_number_verified true, and email with email_verified after an email confirmation." /><br />
</p>

## WhatsApp

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-whatsapp-cross-device.webp" width="800" alt="Signing in on a computer with WhatsApp: the phone camera scans the QR code, WhatsApp opens a ready-made message, the user sends it, taps Confirm, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A QR code, the phone camera, a ready-made message, one tap in WhatsApp.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-whatsapp-phone.webp" width="800" alt="Signing in on a phone with WhatsApp: the user taps &quot;Sign in with WhatsApp&quot;, sends the ready-made message, taps Confirm, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A channel button, a ready-made message, one tap in WhatsApp.</sub>
</p>

### Confirming a purchase

<p align="center">
  <img src="./media/readme/en-whatsapp-confirm.webp" width="800" alt="Confirming a purchase with WhatsApp: the app shows a QR code, the phone camera scans it, the user sends a ready-made message, taps Confirm, and the chat replies &quot;Purchase confirmed&quot;." /><br />
  <sub>The app shows a QR code, the phone camera scans it, the person sends a ready-made message and taps Confirm.</sub>
</p>

### Claims

<p align="center">
  <img src="./media/readme/en-whatsapp-claims.webp" width="720" alt="Claims your app can get from signing in with WhatsApp: sub, amr, channel_type, channel_user_id, name, given_name, preferred_username, phone_number, phone_number_verified true, and email with email_verified after an email confirmation." /><br />
</p>

## Messenger

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-messenger-cross-device.webp" width="800" alt="Signing in on a computer with Messenger: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Messenger chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A QR code, the phone camera, one tap in Messenger.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-messenger-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Messenger&quot;, confirms in the Messenger chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A channel button, one tap in Messenger.</sub>
</p>

### Confirming a purchase

<p align="center">
  <img src="./media/readme/en-messenger-confirm.webp" width="800" alt="Confirming a purchase: the app shows a QR code for a $99 plan, the phone camera scans it, the user confirms in Messenger, and the chat replies &quot;Purchase confirmed&quot;." /><br />
  <sub>The app shows a QR code, the phone camera scans it, the person confirms in Messenger.</sub>
</p>

### Claims

<p align="center">
  <img src="./media/readme/en-messenger-claims.webp" width="720" alt="Claims your app can get from signing in with Messenger: sub, amr, channel_type, channel_user_id, name, given_name, family_name, locale, picture, phone_number, phone_number_verified false, and email with email_verified after an email confirmation." /><br />
</p>

## Instagram

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-instagram-cross-device.webp" width="800" alt="Signing in on a computer with Instagram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Instagram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A QR code, the phone camera, one tap in Instagram.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-instagram-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Instagram&quot;, confirms in the Instagram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>A channel button, one tap in Instagram.</sub>
</p>

### Confirming an action

<p align="center">
  <img src="./media/readme/en-instagram-confirm.webp" width="800" alt="Confirming an action: the app shows a QR code for deleting a project, the phone camera scans it, the user confirms in Instagram, and the chat replies &quot;Confirmed&quot;." /><br />
  <sub>The app shows a QR code, the phone camera scans it, the person confirms in Instagram.</sub>
</p>

### Claims

<p align="center">
  <img src="./media/readme/en-instagram-claims.webp" width="720" alt="Claims your app can get from signing in with Instagram: sub, amr, channel_type, channel_user_id, name, preferred_username, picture, phone_number, phone_number_verified false, and email with email_verified after an email confirmation." /><br />
</p>

## Google

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-google-cross-device.webp" width="800" alt="Signing in on a computer with Google: the phone camera scans the QR code, the user picks a Google account, taps Yes on the confirmation page, and sees &quot;Sign-in confirmed&quot;." /><br />
  <sub>A QR code, the phone camera, a Google account, one tap on the confirmation page.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-google-phone.webp" width="800" alt="Signing in on a phone with Google: the user taps &quot;Sign in with Google&quot;, picks an account, allows access on the first sign-in, taps Yes, and sees &quot;Sign-in confirmed&quot;." /><br />
  <sub>A channel button, a Google account, one tap on the confirmation page. Google asks for consent only on the first sign-in.</sub>
</p>

### Claims

<p align="center">
  <img src="./media/readme/en-google-claims.webp" width="720" alt="Claims your app can get from signing in with Google: sub, amr, channel_type, channel_user_id, name, email, email_verified, given_name, family_name, locale, picture." /><br />
</p>

## Email

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-email-cross-device.webp" width="800" alt="Signing in on a computer by email: QR code, phone camera, the user sends a ready-made email, the page verifies the sender, and the user confirms the sign-in on the computer." /><br />
  <sub>A QR code, the phone camera, a ready-made email, one tap on the confirmation page.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-email-phone.webp" width="800" alt="Signing in on a phone by email: the user taps the email button and sends a ready-made email, the page verifies the sender, and the user confirms the sign-in." /><br />
  <sub>A channel button, a ready-made email, one tap on the confirmation page.</sub>
</p>

### Claims

<p align="center">
  <img src="./media/readme/en-email-claims.webp" width="720" alt="Claims your app can get from signing in with Email: sub, amr, channel_type, channel_user_id, name, email, email_verified, preferred_username, veriqa_channel, veriqa_email_mode." /><br />
</p>
