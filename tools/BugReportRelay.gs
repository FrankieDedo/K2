// K2 bug-report relay — Google Apps Script web app.
// Deploy: script.google.com > New project > paste this > Deploy > New deployment >
//   type "Web app", Execute as: Me, Who has access: Anyone > copy the /exec URL into
//   BugReportService.Endpoint (K2.App/Services/BugReportService.cs).
// Set TOKEN below to the same value as BugReportService.Token.
// Mail goes to the account that owns the script (consumer Gmail quota: ~100 mails/day).

const TOKEN = 'k2-bugreport-change-me';
const MAX_BASE64 = 22 * 1024 * 1024; // keep under Gmail's 25 MB attachment limit

function doPost(e) {
  try {
    const d = JSON.parse(e.postData.contents);
    if (d.token !== TOKEN) return out({ ok: false, error: 'unauthorized' });
    if (!d.zipBase64 || d.zipBase64.length > MAX_BASE64) return out({ ok: false, error: 'bad size' });

    const blob = Utilities.newBlob(Utilities.base64Decode(d.zipBase64), 'application/zip',
      String(d.filename || 'K2-bugreport.zip').replace(/[^\w.\-]/g, '_'));
    const to = Session.getEffectiveUser().getEmail();
    const body = 'K2 ' + d.version + '\nContact: ' + (d.contact || '(none)') +
      '\n\n' + String(d.description || '').slice(0, 20000);
    MailApp.sendEmail({
      to: to,
      subject: '[K2 bug report] ' + d.version + ' — ' + String(d.description || '').slice(0, 60).replace(/\s+/g, ' '),
      body: body,
      attachments: [blob],
    });
    return out({ ok: true });
  } catch (err) {
    return out({ ok: false, error: String(err) });
  }
}

function out(o) {
  return ContentService.createTextOutput(JSON.stringify(o)).setMimeType(ContentService.MimeType.JSON);
}
