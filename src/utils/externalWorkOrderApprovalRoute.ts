const EXTERNAL_WORK_ORDER_APPROVAL_PATH = /^\/external\/work-order-approval\/([0-9a-f-]{36})\/?$/i;

export function getExternalWorkOrderApprovalPublicId(pathname = window.location.pathname) {
  return pathname.match(EXTERNAL_WORK_ORDER_APPROVAL_PATH)?.[1] ?? '';
}

export function isExternalWorkOrderApprovalPath(pathname = window.location.pathname) {
  return Boolean(getExternalWorkOrderApprovalPublicId(pathname));
}
