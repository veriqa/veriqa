# Gallery

This section shows real screens for the main scenarios, step by step.

The screenshots were taken from demo applications and samples running on the Veriqa core, version 0.7.0.

Back to the [README](../README.md). Documentation: [docs.md](./docs.md). Installation: [INSTALL.md](./INSTALL.md).

## General scheme

A sign-in starts in your app: it sends the browser to the sign-in page with a standard OpenID Connect redirect. The user
confirms in a channel, and the browser returns to your app with a code that your app exchanges for tokens.

<p align="center">
  <img src="./media/readme/en-flow-sign-in.webp" width="800" alt="Signing in through Veriqa: your app sends the browser to the sign-in page by an OpenID Connect redirect, the person confirms in a channel, and Veriqa redirects back so your app can exchange the code for tokens." /><br />
</p>

An action confirmation starts on your server: it creates a transaction through the Veriqa API. The user confirms in a
channel, and your server reads the answer and performs the action. The browser is not redirected.

<p align="center">
  <img src="./media/readme/en-flow-confirm.webp" width="800" alt="Confirming an action through Veriqa: your server creates the transaction, the person scans the QR code your app shows and confirms in a channel, and your server reads the answer and performs the action." /><br />
</p>

See [Basic scenarios](https://veriqa.app/docs/basic-scenarios).

## Telegram

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-telegram-cross-device.webp" width="800" alt="Signing in on a computer with Telegram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Telegram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user scans the QR code with their phone camera and confirms in the Telegram chat.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-telegram-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Telegram&quot;, confirms in the Telegram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user taps "Sign in with Telegram" and confirms in the Telegram chat.</sub>
</p>

### Step-up: confirming an action

<p align="center">
  <img src="./media/readme/en-telegram-confirm.webp" width="800" alt="Confirming an action: the app shows a QR code for deleting a project, the phone camera scans it, the user gets the request in Telegram, and the decision goes back to the app." /><br />
  <sub>The app shows a QR code, and the user scans it with their phone camera and declines the request in Telegram. The app receives the answer.</sub>
</p>

### Collecting extra details at sign-in

<p align="center">
  <img src="./media/readme/en-telegram-progressive.webp" width="270" alt="Collecting extra details at sign-in: after confirming in Telegram, the user shares a phone number, fills in the missing fields of a short form, confirms the email with one ready-made message, and the app receives the verified claims." /><br />
  <sub>At sign-in, your app gets what the messaging app doesn't provide: a phone number, answers to a short form and a confirmed email address.</sub>
</p>

The last frame of the animation shows test values from a real ID token.

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-telegram-claims.webp" width="720" alt="Claims your app can get from signing in with Telegram: sub, amr, channel_type, channel_user_id, name, given_name, family_name, preferred_username, locale, picture, phone_number, phone_number_verified true, and email with email_verified after an email confirmation." /><br />
</p>

## WhatsApp

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-whatsapp-cross-device.webp" width="800" alt="Signing in on a computer with WhatsApp: the phone camera scans the QR code, WhatsApp opens a ready-made message, the user sends it, taps Confirm, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user scans the QR code with their phone camera, sends the ready-made message in WhatsApp and taps Confirm.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-whatsapp-phone.webp" width="800" alt="Signing in on a phone with WhatsApp: the user taps &quot;Sign in with WhatsApp&quot;, sends the ready-made message, taps Confirm, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user taps "Sign in with WhatsApp", sends the ready-made message and taps Confirm.</sub>
</p>

### Step-up: confirming an action

<p align="center">
  <img src="./media/readme/en-whatsapp-confirm.webp" width="800" alt="Confirming a purchase with WhatsApp: the app shows a QR code, the phone camera scans it, the user sends a ready-made message, taps Confirm, and the chat replies &quot;Purchase confirmed&quot;." /><br />
  <sub>The app shows a QR code, and the user scans it with their phone camera, sends the ready-made message in WhatsApp and taps Confirm.</sub>
</p>

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-whatsapp-claims.webp" width="720" alt="Claims your app can get from signing in with WhatsApp: sub, amr, channel_type, channel_user_id, name, given_name, preferred_username, phone_number, phone_number_verified true, and email with email_verified after an email confirmation." /><br />
</p>

## Messenger

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-messenger-cross-device.webp" width="800" alt="Signing in on a computer with Messenger: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Messenger chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user scans the QR code with their phone camera and confirms in the Messenger chat.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-messenger-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Messenger&quot;, confirms in the Messenger chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user taps "Sign in with Messenger" and confirms in the Messenger chat.</sub>
</p>

### Step-up: confirming an action

<p align="center">
  <img src="./media/readme/en-messenger-confirm.webp" width="800" alt="Confirming a purchase: the app shows a QR code for a $99 plan, the phone camera scans it, the user confirms in Messenger, and the chat replies &quot;Purchase confirmed&quot;." /><br />
  <sub>The app shows a QR code, and the user scans it with their phone camera and confirms the purchase in Messenger.</sub>
</p>

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-messenger-claims.webp" width="720" alt="Claims your app can get from signing in with Messenger: sub, amr, channel_type, channel_user_id, name, given_name, family_name, locale, picture, phone_number, phone_number_verified false, and email with email_verified after an email confirmation." /><br />
</p>

## Instagram

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-instagram-cross-device.webp" width="800" alt="Signing in on a computer with Instagram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Instagram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user scans the QR code with their phone camera and confirms in the Instagram chat.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-instagram-phone.webp" width="540" alt="Signing in on a phone: the user taps &quot;Sign in with Instagram&quot;, confirms in the Instagram chat, and the chat replies &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user taps "Sign in with Instagram" and confirms in the Instagram chat.</sub>
</p>

### Step-up: confirming an action

<p align="center">
  <img src="./media/readme/en-instagram-confirm.webp" width="800" alt="Confirming an action: the app shows a QR code for deleting a project, the phone camera scans it, the user confirms in Instagram, and the chat replies &quot;Confirmed&quot;." /><br />
  <sub>The app shows a QR code, and the user scans it with their phone camera and confirms the deletion in Instagram.</sub>
</p>

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-instagram-claims.webp" width="720" alt="Claims your app can get from signing in with Instagram: sub, amr, channel_type, channel_user_id, name, preferred_username, picture, phone_number, phone_number_verified false, and email with email_verified after an email confirmation." /><br />
</p>

## Google

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-google-cross-device.webp" width="800" alt="Signing in on a computer with Google: the phone camera scans the QR code, the user picks a Google account, taps Yes on the confirmation page, and sees &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user scans the QR code with their phone camera, picks a Google account and taps Yes on the confirmation page.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-google-phone.webp" width="800" alt="Signing in on a phone with Google: the user taps &quot;Sign in with Google&quot;, picks an account, allows access on the first sign-in, taps Yes, and sees &quot;Sign-in confirmed&quot;." /><br />
  <sub>The user taps "Sign in with Google", picks an account and taps Yes on the confirmation page. Google asks for consent only on the first sign-in.</sub>
</p>

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-google-claims.webp" width="720" alt="Claims your app can get from signing in with Google: sub, amr, channel_type, channel_user_id, name, email, email_verified, given_name, family_name, locale, picture." /><br />
</p>

## Email

### Signing in on a computer

<p align="center">
  <img src="./media/readme/en-email-cross-device.webp" width="800" alt="Signing in on a computer by email: QR code, phone camera, the user sends a ready-made email, the page verifies the sender, and the user confirms the sign-in on the computer." /><br />
  <sub>The user scans the QR code with their phone camera, sends the ready-made email and confirms the sign-in on the computer.</sub>
</p>

### Signing in on a phone

<p align="center">
  <img src="./media/readme/en-email-phone.webp" width="800" alt="Signing in on a phone by email: the user taps the email button and sends a ready-made email, the page verifies the sender, and the user confirms the sign-in." /><br />
  <sub>The user taps "Send confirmation email", sends the ready-made email and taps Yes on the confirmation page.</sub>
</p>

### Claims your app can get

<p align="center">
  <img src="./media/readme/en-email-claims.webp" width="720" alt="Claims your app can get from signing in with Email: sub, amr, channel_type, channel_user_id, name, email, email_verified, preferred_username, veriqa_channel, veriqa_email_mode." /><br />
</p>
