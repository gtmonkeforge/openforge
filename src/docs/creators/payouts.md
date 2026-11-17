---
title: Getting paid
section: Creators
order: 1
description: Set up Stripe to sell mods, offer memberships and accept donations, while keeping your personal details away from buyers.
---
# Getting paid

You can sell mods, offer monthly memberships and accept donations on MonkeForge. Payments go through Stripe, a payment company used by millions of online shops. This guide explains how to set it up and how MonkeForge keeps your personal details away from buyers.

## Who sees what

| Your information | Who sees it |
|---|---|
| Legal name, date of birth, home address, ID, bank account | **Only Stripe.** The law requires Stripe to check who gets paid. MonkeForge never sees these, and buyers never do. |
| Your public handle, for example `CoolModder` | Buyers, on checkout and on receipts |
| MonkeForge's support email, phone and address | Buyers, on receipts, in place of yours |
| `MONKEFORGE COOLMODDER` | Buyers, on their bank or card statement |

The second half of this table only holds while your **public details** in Stripe stay set to what MonkeForge filled in. The rest of this guide explains how to keep them that way.

## Before you start

- **You must be 18 or older** to have your own Stripe account. If you're younger, a parent or guardian has to set it up in their name, and the money goes to their bank account. Their personal details are protected the same way.
- Have your bank details and a photo ID ready. Stripe may ask for them.

## Connecting Stripe

1. On MonkeForge, go to [**Settings → Payouts**](/settings/payouts) and click **Connect with Stripe**.
2. Stripe asks for your personal details, ID and bank account. Fill these in truthfully. They go to Stripe only.
3. Stripe also shows a few **public details** that MonkeForge has already filled in:
   - **Public business name:** your MonkeForge handle
   - **Support email, phone and address:** MonkeForge's
   - **Statement descriptor:** something like `MONKEFORGE COOLMODDER`
   - **Website:** your MonkeForge profile page

   **Leave these as they are.** Don't replace them with your own phone number or address, even if Stripe suggests it.
4. When Stripe sends you back to MonkeForge, the Payouts page shows **Ready to sell** once Stripe has approved your account. This can take a few minutes, or longer if Stripe needs more documents.

## Keeping your details private

MonkeForge checks your public Stripe details every time they change, and once a day.

If any of them would show buyers something personal, MonkeForge **pauses your paid sales**, and **pauses billing for your members**. Members keep their access, but nobody is charged and no receipt is sent until you fix it. This protects you, not just MonkeForge: otherwise each receipt would show buyers your phone or address.

When this happens:

- You get a notification, and the Payouts page shows **Sales paused**. Its **Your public Stripe details** panel shows what buyers see, marks each field that needs fixing as **Needs changing**, and gives the exact value to enter.
- Click **Edit public details in Stripe**. That opens [**Settings → Business → Public details**](https://dashboard.stripe.com/settings/public) in your Stripe dashboard.
- Enter the values shown on the Payouts page, save, and refresh the Payouts page. Sales and billing start again right away.

**If you connected Stripe before this check existed,** your account probably still has your own details in it. You get **14 days** after the first notice to change them before sales pause. You'll see the deadline on the Payouts page.

**Things that are fine to change:** the website field (only use a page that doesn't identify you), your branding colours and logo in Stripe, and your payout schedule.

## Refunds and disputes

- If a buyer asks MonkeForge for a refund, MonkeForge staff handle it. The money comes back out of your Stripe balance, and MonkeForge returns its 10% fee too.
- If a buyer disputes a charge with their bank (a "chargeback"), Stripe tells you in your Stripe dashboard and asks for evidence. **Don't include your real name, address, phone number or personal email in your evidence.** The buyer's bank can see everything you submit. Your MonkeForge handle and screenshots of the mod page and sales records are enough.
- Disputes come with a fee from Stripe, which is taken from your Stripe balance. Answering buyers' refund requests quickly is the best way to avoid them.

## Fees

- **Paid mods and memberships:** MonkeForge keeps 10%. Stripe's card processing fee also comes out of your share. The Payouts page shows both.
- **Donations:** you receive the full amount.

## Taxes

Money you earn from selling mods may be taxable where you live. In the US, Stripe may send you a tax form (1099-K) if you earn enough in a year. Keep your own records, and ask a tax professional or a parent if you're unsure.

## Questions

Contact MonkeForge support with anything about payouts. Questions about your Stripe account itself, such as verification or bank transfers, go to Stripe support from your Stripe dashboard.
