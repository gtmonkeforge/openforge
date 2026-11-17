# Payments setup (Stripe)

OpenForge sells mods and takes donations through Stripe Connect. Creators connect their own Stripe account; OpenForge never handles their bank or identity details.

## How money moves

| Payment | Stripe charge type | Who pays Stripe's fee | MonkeForge's cut |
|---|---|---|---|
| Paid mod | Direct charge on the creator's account | Creator | 10% application fee |
| Monthly tier (mod or creator membership) | Subscription on the creator's account | Creator | 10% of every invoice |
| Donation to a creator | Destination charge from the platform account | MonkeForge | None, creator gets 100% |
| Donation to MonkeForge | Platform charge | MonkeForge | All of it |

Creator accounts are created with `controller.stripe_dashboard.type=full`, `fees.payer=account` and `losses.payments=stripe`. Creators get their own full Stripe dashboard and handle refunds and disputes on their own sales. Because MonkeForge is the merchant for creator donations, MonkeForge is liable for disputes on those.

## Checkout branding

Paid mods and memberships are charged on the creator's account, so Stripe Checkout would normally show the creator's own Stripe branding. OpenForge overrides it on every Checkout Session with `branding_settings`: the site's name, its accent colour on the buttons and, if available, its logo. A line under the pay button tells the buyer which creator they're paying and what the charge will look like on their statement, because Stripe still shows the creator's public business name in its terms and on the card statement.

Stripe can't display SVG, so set `STRIPE_BRAND_LOGO_URL` to a PNG or JPEG for the logo. If you set a PNG or JPEG `BRAND_LOGO`, it's used automatically. Without either, checkout shows the name only.

## Creator privacy

Paid mods and memberships are charged on the creator's own Stripe account, so buyers see that account's **public details** on receipts, invoices and bank statements. Most creators are individuals with no business contact, so without help those would be their legal name, home phone and home address. OpenForge keeps them private in three ways:

1. **Pre-filled accounts.** New creator accounts are created with the site's support email, phone and address, the creator's public handle as the business name, and a statement descriptor such as `MONKEFORGE COOLMOD`. Creators can't connect Stripe until all `SUPPORT_*` settings are set.
2. **A check on every change.** Each `account.updated` webhook, each visit to the Payouts page, and a daily background sweep compare the account's public details with the site's. If the support email, phone or address differs, the business name is empty, or the statement descriptor doesn't start with the prefix, the account **can't sell**: paid checkouts and new memberships are refused, and billing on its existing memberships is paused (`pause_collection[behavior]=void`), so no receipt goes out from it. Members keep access while billing is paused. Billing resumes once the details are fixed. A field Stripe left empty passes, because it shows buyers nothing.
3. **A grace period for existing accounts.** Accounts connected before this check existed get 14 days from the first failed check before sales stop. Accounts the site pre-filled, and any account once it passes, get no grace period.

Creators get a notification when their account first fails the check. The Payouts page lists the fields to change, the exact values to enter, and a link to Stripe's public details page. Creator donations are charged on the platform account and never show creator details, so they aren't affected.

What this can't cover:

- A creator can still type personal details into Stripe. If they do, sales stop within seconds, when the webhook arrives. A sale already in progress can still complete.
- Disputes on creator accounts are answered by the creator in their own Stripe dashboard. Anything they include as evidence goes to the buyer's bank.
- Some banking apps show a merchant's city or phone next to the statement line. Check what yours show with a small live purchase before relying on it.
- Before launch, ask Stripe support to confirm that creators listing the site's support contact is acceptable for your account.

The support address is only compared on line 1, line 2, city, postal code and country. The state is left out because Stripe may store it in a different form.

## Receipts

Every paid purchase, membership invoice and donation sends the buyer a receipt from the site's email address, and so does every full refund. Receipts go to the buyer's confirmed address and are always sent, whatever their email preferences. Email has to be configured; see the SMTP settings in [SIGN_IN.md](SIGN_IN.md). If you don't want buyers to also get Stripe's own receipts, turn them off under **Settings → Customer emails** in Stripe. For paid mods and memberships, that setting lives on each creator's account.

## Configuration

Put these in `.env` or the server environment:

```
# Comments must be on their own line: anything after the = is part of the value.

# Platform secret key (sk_test_... while testing)
STRIPE_SECRET_KEY=sk_live_...
# Signing secret of the "Your account" endpoint
STRIPE_WEBHOOK_SECRET=whsec_...
# Signing secret of the "Connected accounts" endpoint
STRIPE_CONNECT_WEBHOOK_SECRET=whsec_...
# Optional, defaults to usd
STRIPE_CURRENCY=usd
# Optional, 32 random bytes in base64, see below
PAYLOAD_KEY=...

# Creator privacy: required before creators can connect Stripe, see "Creator privacy" below
SUPPORT_EMAIL=support@example.com
SUPPORT_PHONE=+15125550123
SUPPORT_ADDRESS_LINE1=123 Example St
# Optional
SUPPORT_ADDRESS_LINE2=Suite 4
SUPPORT_ADDRESS_CITY=Austin
# Optional where the country has no states or provinces
SUPPORT_ADDRESS_STATE=TX
SUPPORT_ADDRESS_POSTAL_CODE=78701
# Two-letter country code
SUPPORT_ADDRESS_COUNTRY=US
# Optional, defaults to the brand name: 2-10 letters or digits
STRIPE_STATEMENT_PREFIX=MONKEFORGE
# Optional, a PNG or JPEG shown at the top of Checkout
STRIPE_BRAND_LOGO_URL=https://example.com/logo.png
```

Payments stay switched off, and the site says so, until the secret key and at least one webhook secret are set.

## Stripe Dashboard steps

1. Turn on **Connect** for the platform account and complete the platform profile.
2. Create **two** webhook endpoints, both pointing at `https://<your site>/api/v1/stripe/webhook`:
   - **Events on your account:** `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `charge.refunded`, `charge.dispute.created`, `charge.dispute.closed`
   - **Events on Connected accounts:** the same events, plus `account.updated`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted` and `invoice.paid`

   Selecting **all events** on either endpoint also works: the handler acknowledges any event type it doesn't use without writing to the database. The exact lists above keep traffic and Stripe's delivery logs smaller, though.

   Paid-mod checkouts and memberships happen on the creator's account, so their completion, renewal, refund and dispute events arrive only on the Connected-accounts endpoint. The webhook handler reads both the older and the newer (2025 "basil" and later) invoice and subscription layouts, so either API version works.

3. Copy each endpoint's signing secret into the two variables above.

Access is granted only from a signed webhook, never from the browser returning from Checkout. The buyer's page polls for a few seconds until the webhook lands.

## Refunds, disputes and takedowns

- Buyers can open a dispute with staff from their profile within 90 days of a payment. Staff handle these in **Admin → Disputes**.
- Accepting a dispute refunds the full amount on the creator's account, including MonkeForge's fee (`refund_application_fee`). For a membership it refunds the latest payment and ends the subscription immediately.
- Taking a mod down (unapprove or delete) retires its tiers and cancels their subscriptions immediately. Banning a user does the same for every tier they own. If Stripe can't be reached, the admin panel tells you how many subscriptions it couldn't cancel. Cancel those by hand in the Stripe dashboard.
- A card chargeback on a membership payment cancels that membership automatically.

## Payload key

Injector mod binaries are sealed at rest with a 256-bit key. If `PAYLOAD_KEY` is unset, the key is generated at `<DATA_DIR>/keys/payload.key` on first start, with owner-only permissions.

**Back this key up separately from the data directory.** Without it, every sealed mod is unreadable and creators have to re-upload. If you set `PAYLOAD_KEY` yourself, generate it with `openssl rand -base64 32`.

## Testing locally

Use test-mode keys and forward events with the Stripe CLI:

```
stripe listen --forward-to localhost:5264/api/v1/stripe/webhook --forward-connect-to localhost:5264/api/v1/stripe/webhook
```

The CLI prints one signing secret that covers both streams, so set it as `STRIPE_WEBHOOK_SECRET`.
