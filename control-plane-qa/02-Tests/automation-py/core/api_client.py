"""HTTP API client wrapper with error handling, cert skip, and retry logic."""

import json
from typing import Any, Dict, Optional

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry as UrllibRetry


class ApiClient:
    """Wrapper around requests.Session with retry logic and error handling."""

    def __init__(
        self,
        base_url: str,
        skip_certificate_check: bool = True,
        api_version: str = "1",
        timeout_seconds: int = 30,
    ):
        """Initialize API client.

        Args:
            base_url: Base URL for API (e.g., https://featbit-api.west.local)
            skip_certificate_check: Whether to skip TLS certificate verification
            api_version: API version (default "1")
            timeout_seconds: Request timeout in seconds
        """
        self.base_url = base_url.rstrip("/")
        self.api_version = api_version
        self.timeout_seconds = timeout_seconds

        self.session = requests.Session()

        # Configure retry strategy: 3 retries with exponential backoff
        retry_strategy = UrllibRetry(
            total=3,
            backoff_factor=1,
            status_forcelist=[429, 500, 502, 503, 504],
            allowed_methods=["HEAD", "GET", "OPTIONS", "POST", "PUT"],
        )

        adapter = HTTPAdapter(max_retries=retry_strategy)
        self.session.mount("http://", adapter)
        self.session.mount("https://", adapter)

        if skip_certificate_check:
            self.session.verify = False
            import urllib3

            urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

    def request(
        self,
        method: str,
        endpoint: str,
        headers: Optional[Dict[str, str]] = None,
        body: Optional[Any] = None,
        **kwargs: Any,
    ) -> Dict[str, Any]:
        """Make HTTP request to API.

        Args:
            method: HTTP method (GET, POST, PUT, etc.)
            endpoint: API endpoint (e.g., /api/v1/user/profile)
            headers: Optional headers dict
            body: Optional request body (object or string)
            **kwargs: Additional arguments passed to requests

        Returns:
            Parsed JSON response

        Raises:
            requests.RequestException: On HTTP errors
        """
        url = f"{self.base_url}{endpoint}"

        request_headers = headers or {}
        request_headers.setdefault("Content-Type", "application/json")

        request_body = None
        if body is not None:
            if isinstance(body, str):
                request_body = body
            else:
                request_body = json.dumps(body, default=str)

        try:
            response = self.session.request(
                method=method,
                url=url,
                headers=request_headers,
                data=request_body,
                timeout=self.timeout_seconds,
                **kwargs,
            )
            response.raise_for_status()
            return response.json() if response.text else {}

        except requests.exceptions.RequestException as e:
            status_code = None
            response_text = None

            response = getattr(e, "response", None)
            if response is not None:
                status_code = response.status_code
                response_text = (response.text or "").strip()
                if len(response_text) > 500:
                    response_text = response_text[:500] + "..."

            details = f"API request failed: {method} {endpoint}"
            if status_code is not None:
                details += f" status={status_code}"
            if response_text:
                details += f" body={response_text}"

            raise ApiClientError(details) from e

    def get(
        self,
        endpoint: str,
        headers: Optional[Dict[str, str]] = None,
        **kwargs: Any,
    ) -> Dict[str, Any]:
        """GET request."""
        return self.request("GET", endpoint, headers, **kwargs)

    def post(
        self,
        endpoint: str,
        body: Optional[Any] = None,
        headers: Optional[Dict[str, str]] = None,
        **kwargs: Any,
    ) -> Dict[str, Any]:
        """POST request."""
        return self.request("POST", endpoint, headers, body, **kwargs)

    def put(
        self,
        endpoint: str,
        body: Optional[Any] = None,
        headers: Optional[Dict[str, str]] = None,
        **kwargs: Any,
    ) -> Dict[str, Any]:
        """PUT request."""
        return self.request("PUT", endpoint, headers, body, **kwargs)


class ApiClientError(Exception):
    """Raised when API client encounters an error."""

    pass


def extract_data(response: Dict[str, Any]) -> Any:
    """Extract 'data' field from API response, handling case variations.

    Args:
        response: Response dict from API

    Returns:
        Value of 'data' or 'Data' field, or the response itself if no data field
    """
    if not response:
        return None
    if isinstance(response, dict):
        if "data" in response:
            return response["data"]
        if "Data" in response:
            return response["Data"]
    return response
