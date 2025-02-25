import React, { Component } from "react";
import PropTypes from "prop-types";
import { userRoles, userType, comment, commentStatus, commentPolicy } from "../commonPropTypes";
import moment from "moment";
import { Link, Hint } from "ui";
import Avatar from "../../common/Avatar/Avatar";
import CommentSendForm from "../CommentSendForm/CommentSendForm";
import Like from "./Like/Like";
import KebabActions from "./Kebab/KebabActions";
import Header from "./Header/Header";
import Marks from "./Marks/Marks";
import CommentActions from "./CommentActions/CommentActions";
import scrollToView from "src/utils/scrollToView";
import { convertDefaultTimezoneToLocal } from "src/utils/momentUtils";

import styles from "./Comment.less";
import { AccessType, CourseRoleType } from "src/consts/accessType";

class Comment extends Component {
	constructor(props) {
		super(props);

		this.state = {
			isApproved: props.comment.isApproved
		};

		this.ref = React.createRef();
	}


	componentDidMount() {
		this.scrollToComment();
	};

	componentDidUpdate(prevProps, prevState, snapshot) {
		if(this.props.isSlideReady && !prevProps.isSlideReady){
			this.scrollToComment();
		}
	}

	scrollToComment = () =>{
		if(this.props.isSlideReady && window.location.hash === `#comment-${ this.props.comment.id }`) {
			scrollToView(this.ref);
		}
	}

	render() {
		const {children, commentEditing, comment, userRoles, user} = this.props;
		const canViewProfiles = (user.systemAccesses && user.systemAccesses.includes(AccessType.viewAllProfiles)) ||
			userRoles.isSystemAdministrator;
		const profileUrl = `${window.location.origin}/Account/Profile?userId=${comment.author.id}`;

		return (
			<div className={`${styles.comment} ${!this.state.isApproved ? styles.isNotApproved : ""}`} ref={this.ref}>
				<span className={styles.commentAnchor} id={`comment-${this.props.comment.id}`} />
				{canViewProfiles ? <Link href={profileUrl}><Avatar user={comment.author} size="big" /></Link> :
					<Avatar user={comment.author} size="big" />}
				<div className={styles.content}>
					{this.renderHeader(profileUrl, canViewProfiles)}
					<div className={styles.timeSinceAdded}>
						<Hint
							pos="right middle"
							text={`${moment(convertDefaultTimezoneToLocal(comment.publishTime)).format("DD MMMM YYYY в HH:mm")}`}>
							{moment(convertDefaultTimezoneToLocal(comment.publishTime)).startOf("minute").fromNow()}
						</Hint>
					</div>
					{comment.id === commentEditing.commentId ? this.renderEditCommentForm() : this.renderComment()}
					{children}
				</div>
			</div>
		);
	}

	renderHeader(profileUrl, canViewProfiles) {
		const {actions, comment, userRoles, user, slideType, getUserSolutionsUrl, courseId} = this.props;
		const canSeeKebabActions = user.id && (user.id === comment.author.id ||
			this.canModerateComments(userRoles, AccessType.editPinAndRemoveComments) ||
			this.canModerateComments(userRoles, AccessType.viewAllStudentsSubmissions));

		return (
			<Header
				profileUrl={profileUrl}
				canViewProfiles={canViewProfiles}
				name={comment.author.visibleName}>
				<Like
					canLike={user.isAuthenticated}
					isLiked={comment.isLiked}
					count={comment.likesCount}
					onClick={() => actions.handleLikeClick(comment.id, comment.isLiked)} />
				<Marks
					authorGroups={comment.authorGroups}
					courseId={courseId}
					canViewStudentsGroup={this.canViewStudentsGroup}
					comment={comment} />
				{canSeeKebabActions &&
				<KebabActions
					user={user}
					url={getUserSolutionsUrl(comment.author.id)}
					canModerateComments={this.canModerateComments}
					userRoles={userRoles}
					slideType={slideType}
					comment={comment}
					actions={actions}
					handleCommentBackGround={this.handleCommentBackground} />}
			</Header>
		);
	}

	renderComment() {
		const {comment, user, userRoles, hasReplyAction, actions, slideType,
			getUserSolutionsUrl} = this.props;

		return (
			<>
				<p className={styles.text}>
					<span className={styles.textFromServer}
						  dangerouslySetInnerHTML={{ __html: comment.renderedText }} />
				</p>
				{user.id &&
				<CommentActions
					slideType={slideType}
					comment={comment}
					canReply={this.canReply(userRoles)}
					user={user}
					url={getUserSolutionsUrl(comment.author.id)}
					userRoles={userRoles}
					hasReplyAction={hasReplyAction}
					actions={actions}
					canModerateComments={this.canModerateComments} />}
			</>
		);
	}

	renderEditCommentForm() {
		const {comment, actions, commentEditing} = this.props;
		const focusedEditForm = {inEditForm: comment.id === commentEditing.commentId,};

		return (
			<CommentSendForm
				isShowFocus={focusedEditForm}
				commentId={comment.id}
				handleSubmit={actions.handleEditComment}
				text={comment.text}
				submitTitle={"Сохранить"}
				sending={commentEditing.sending}
				handleCancel={() => actions.handleShowEditForm(null)} />
		);
	}

	canModerateComments = (role, accesses) => {
		return role.isSystemAdministrator || role.courseRole === CourseRoleType.courseAdmin ||
			(role.courseRole === CourseRoleType.instructor && role.courseAccesses.includes(accesses));
	};

	canReply = (role) => {
		const {commentPolicy} = this.props;
		return (commentPolicy.areCommentsEnabled && ((role.courseRole === CourseRoleType.student || role.courseRole === null ||
			role.courseRole.length === 0) || role.isSystemAdministrator || role.courseRole === CourseRoleType.courseAdmin ||
			role.courseRole === CourseRoleType.instructor));
	};

	canViewStudentsGroup = () => {
		const {userRoles, user} = this.props;

		return (user.systemAccesses && user.systemAccesses.includes(AccessType.viewAllGroupMembers)) ||
			(userRoles.courseAccesses && userRoles.courseAccesses.includes(AccessType.viewAllGroupMembers)) ||
			userRoles.isSystemAdministrator;
	};

	handleCommentBackground = (commentId, isApproved) => {
		this.setState({
			isApproved,
		});
	};
}

Comment.propTypes = {
	user: userType.isRequired,
	userRoles: userRoles.isRequired,
	comment: comment.isRequired,
	actions: PropTypes.objectOf(PropTypes.func),
	children: PropTypes.array,
	getUserSolutionsUrl: PropTypes.func,
	commentEditing: commentStatus,
	commentPolicy: commentPolicy,
	hasReplyAction: PropTypes.bool,
	slideType: PropTypes.string,
	courseId: PropTypes.string,
	isSlideReady: PropTypes.bool,
};

export default Comment;
