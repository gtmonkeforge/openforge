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

## Configuration

Put these in `.env` or the server environment:

```
STRIPE_SECRET_KEY=sk_live_...              # platform secret key (sk_test_... while testing)
STRIPE_WEBHOOK_SECRET=whsec_...            # signing secret of the "Your account" endpoint
STRIPE_CONNECT_WEBHOOK_SECRET=whsec_...    # signing secret of the "Connected accounts" endpoint
STRIPE_CURRENCY=usd                        # optional, defaults to usd
PAYLOAD_KEY=<32 random bytes, base64>      # optional, see below
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
