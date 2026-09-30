import { Link } from 'react-router-dom';

export function NotFoundPage() {
  return (
    <div className="center-page">
      <h1>Page not found</h1>
      <Link className="btn" to="/">Back to Home</Link>
    </div>
  );
}
