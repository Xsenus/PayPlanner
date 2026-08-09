export const PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT = 'pp:payment-advance-review-changed';
export const PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY = 'pp.payments.advanceReviewFilter';

export function notifyPaymentAdvanceReviewChanged() {
  window.dispatchEvent(new Event(PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT));
}
