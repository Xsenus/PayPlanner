import type { PaymentAdvanceReviewStatus } from '../types';

export const PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT = 'pp:payment-advance-review-changed';
export const PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY = 'pp.payments.advanceReviewFilter';
export const PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_EVENT = 'pp:payment-advance-review-filter-intent';

export function requestPaymentAdvanceReviewFilter(filter: PaymentAdvanceReviewStatus) {
  window.sessionStorage.setItem(PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY, filter);
  window.dispatchEvent(new Event(PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_EVENT));
}

export function notifyPaymentAdvanceReviewChanged() {
  window.dispatchEvent(new Event(PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT));
}
