import { app, brand, esc, icon, setTitle } from '../core.js';

const terms = [
    ['Using MonkeForge', 'MonkeForge is a community service for discovering and sharing Gorilla Tag mods. By using the service, you agree to these terms. Follow applicable laws and the rules of any games or platforms you use with downloaded mods. MonkeForge is not affiliated with Another Axiom.'],
    ['Accounts and community conduct', 'Sign-in uses Discord. You are responsible for your account and the content you submit. Do not impersonate others, harass users, misuse the service, bypass access controls, or upload malware or content that violates others’ rights.'],
    ['Uploads and ownership', 'Only upload content you own or have permission to share. You retain your rights and grant MonkeForge permission to store, display, and distribute your submitted content to operate the service. Any license supplied by a mod author governs use of that mod; a download does not transfer ownership.'],
    ['Moderation', 'Administrators may review, reject, or remove uploads and restrict accounts for violations or to protect the community. Availability of a mod does not guarantee that it is safe or compatible with your game.'],
    ['Downloads and availability', 'Mods and the service are provided as available, without guarantees of uninterrupted access, compatibility, or fitness for a particular purpose. Review a mod’s source, permissions, and author instructions before installing it. You are responsible for your choice to install and use mods. To the extent permitted by applicable law, MonkeForge is not responsible for losses caused by third-party mods or interruptions to the service. These terms do not limit rights that cannot legally be excluded.'],
    ['Changes', 'These terms may be updated as the service changes. Updates will appear on this page with a revised date.'],
];

const privacy = [
    ['Information stored', 'When you sign in, MonkeForge receives your Discord user ID, username, display name, and avatar information. It stores account and profile information, appearance preferences, and content you submit, including mods, release files, images, descriptions, and profile links. Discord sign-in requests the identify scope; it does not request your email, password, server memberships, or bot access. Discord access tokens are not persisted.'],
    ['How information is used', 'Information supports sign-in, public author profiles, publishing, downloads, moderation, and saved settings. Administrators can access account information and moderation records to manage the service.'],
    ['Public content', 'Your public profile and approved uploads are visible to visitors. Files you publish can be downloaded and copied by others. Avoid including private information in profiles, descriptions, images, or uploaded files.'],
    ['Cookies and browser storage', 'A session cookie supports sign-in and request security. Browser local storage saves your selected accent color. You can clear these through your browser settings; clearing the session cookie signs you out, and clearing local storage resets browser preferences.'],
    ['Activity data', 'The application records mod view and download activity, including the mod, category, time, and country code when provided by a trusted proxy. These activity records do not store IP addresses or precise coordinates, and the application does not make third-party geolocation requests. Hosting infrastructure may process request information separately.'],
    ['External services', 'Discord handles authentication under its own privacy policy. Links to source code, author websites, and other external services are governed by those services’ policies.'],
    ['Retention and your choices', 'Account information, uploaded content, and activity records are stored to support operation of the service. You can edit your profile and preferences and sign out at any time. For removal of information you cannot edit yourself, contact the site administrator through the community channel where you accessed MonkeForge. Removal from this service cannot remove copies others have downloaded.'],
    ['Policy updates', 'Changes to this policy will be posted here with a revised date.'],
];

export function render(page) {
    const title = page === 'terms' ? 'Terms of Service' : 'Privacy Policy';
    setTitle(title);
    app.innerHTML = `<article class="legal">
        <a class="breadcrumb" href="/browse">${icon('back')}Back to browse</a>
        <div class="page-head"><div><h1>${title}</h1><p class="mono">Last updated October 6, 2026</p></div></div>
        <div class="surface panel">${(page === 'terms' ? terms : privacy).map(([heading, text]) => `<h2>${esc(heading.replaceAll('MonkeForge', brand.name))}</h2><p>${esc(text.replaceAll('MonkeForge', brand.name))}</p>`).join('')}</div>
    </article>`;
}
